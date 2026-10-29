# Web Crawler — job-based crawling system

A user submits a URL; a crawl job is created and processed asynchronously by a worker; pages, the link graph and the
Domain Link Ratio are stored in PostgreSQL and shown live in a React UI.

```
 React SPA (nginx :3000) ──/api (REST + SSE)──▶ Crawl API (.NET 8, :8080) ──publish──▶ RabbitMQ ──consume──▶ Crawl Worker (.NET 8, :8081)
                                                      ▲        │                            ▲                       │
                                                      │        └──────── PostgreSQL ◀───────┼───────────────────────┘
                                                      └──── progress events (fanout) ───────┘
```


## Repository layout

```
backend/                      .NET 8 solution (Crawler.sln)
  src/Crawler.Domain          entities + pure crawling logic (normalization, link extraction, ratio) — no I/O
  src/Crawler.Infrastructure  EF Core model + migrations, RabbitMQ topology/publisher, message contracts, logging
  src/Crawler.Api             orchestrator: REST + SSE, validation, tree assembly, reconciler, migrations, health
  src/Crawler.Worker          consumer, BFS crawl engine, HTTP fetcher (resilience, SSRF guard), progress events, health
  tests/Crawler.Domain.Tests  unit tests (URL normalization, link extraction, ratio)
  tests/Crawler.Worker.Tests  integration tests: real engine + SQLite + local HTML fixtures; fetcher/classifier/policy units
  tests/Crawler.Api.Tests     endpoint tests with WebApplicationFactory (SQLite, fake publisher)
frontend/                     React + Vite + TypeScript SPA (+ nginx config and Dockerfile)
certs/                        optional extra CA certificates trusted by the images (corporate TLS inspection)
docker-compose.yml            postgres · rabbitmq · api · worker · web
.github/workflows/ci.yml      backend build+test · frontend build · docker images
```

Dependency direction is strictly inward: `Api`/`Worker` → `Infrastructure` → `Domain`. The API and the worker never reference each other; they share only the database schema and the message contracts.

## Key architectural choices

1. **Two processes, one trigger message per job.** The API creates the job row and publishes `crawl.job.requested`;
   the worker runs the whole breadth-first crawl for that job. The database row is the source of truth, the message
   is only a trigger — which is what makes at-least-once delivery cheap to handle (see *Idempotency*).
2. **Idempotency by schema, not by hope.** Unique keys on `pages (job_id, url)` and `edges (job_id, parent, child)`
   plus a per-job worker lease; every status transition is a conditional update.
3. **Broker-native resilience.** Transient failures go to a TTL retry queue, poison messages to a DLQ; a reconciler
   closes the publish-after-commit gap instead of a full outbox.
4. **Strict inward layering.** `Domain` (pure crawling logic) ← `Infrastructure` (EF Core, RabbitMQ) ← `Api` / `Worker`;
   the two hosts never reference each other.
5. **Bounded everything.** Page budget bounds the table, not just the fetches; link lists stay off the tree payload;
   bodies, redirects, concurrency and destinations (SSRF guard) are capped.
6. **Progress is pushed, not polled** (SSE over a fanout exchange) with polling as the automatic fallback.

Details and trade-offs follow in the sections below.

## Run locally

Prerequisites: Docker Desktop (Compose v2).

```bash
docker compose up --build
```

| Service      | URL |
|--------------|-----|
| UI           | http://localhost:3000 |
| API          | http://localhost:8080 — Swagger `/swagger`, health `/health`, `/health/ready` |
| Worker (×2)  | no host port; `docker compose exec worker wget -qO- http://localhost:8081/health/ready` |
| RabbitMQ UI  | http://127.0.0.1:15672 (guest / guest) |
| PostgreSQL   | 127.0.0.1:5433 (crawler / crawler) — host port 5433 avoids clashing with a local Postgres |

Credentials default to local-development values; copy `.env.example` to `.env` to change them in one place.
Infrastructure ports are bound to loopback. The API applies EF Core migrations on startup under a Postgres advisory
lock (safe with several replicas); the worker and the API declare the RabbitMQ topology on startup, both retrying
while their dependencies boot.

**Behind a TLS-inspecting proxy?** Drop the proxy's root certificate(s) as `certs/*.crt` before `docker compose build`.
They are trusted by the image builds (NuGet, npm) and by the API/worker at runtime, so HTTPS sites can be crawled.
Nothing changes when the directory is empty.

### Dev loop without containers for the apps

```bash
docker compose up postgres rabbitmq                 # infra only
dotnet run --project backend/src/Crawler.Api        # http://localhost:8080
dotnet run --project backend/src/Crawler.Worker     # http://localhost:8081
cd frontend && npm install && npm run dev           # http://localhost:5173 (proxies /api → :8080)
```

### Tests

```bash
dotnet test backend/Crawler.sln
cd frontend && npm run build
```

## API

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/jobs` | `{ url, maxDepth? (1..5, default 2), maxPages? (1..1000, default 200) }` → `201 { jobId }`; rate-limited per client |
| GET | `/api/jobs/{id}` | Status, timestamps, counters, `progressPercent`, `durationMs`, `failureReason` |
| GET | `/api/jobs/{id}/events` | **SSE** stream of `job` events (same payload as above) until a final `end` event |
| GET | `/api/jobs/{id}/tree` | Hierarchical result `{ jobId, totalPages, root }`; partial while running |
| GET | `/api/jobs/{id}/pages/{pageId}/links` | Every outgoing link of one page `[{ url, isInternal }]` — loaded on demand by the UI |
| GET | `/api/jobs?page=1&pageSize=20` | History, most recent first `{ items, page, pageSize, totalCount }` |
| POST | `/api/jobs/{id}/cancel` | `202` if Pending/Running, `409` if already terminal |
| GET | `/health`, `/health/ready` | Liveness / readiness (Postgres, RabbitMQ; events consumer reports *Degraded* only) |

Errors are RFC 7807 `application/problem+json`. Every response carries `X-Correlation-Id` (echoed if sent by the
client); the same id travels with the job message and appears in worker logs.

## How a crawl works

1. `POST /api/jobs` validates and normalizes the URL, inserts the job (`Pending`), publishes `crawl.job.requested`
   (persistent, publisher-confirmed) and returns the id. If the broker is down the job still exists; the
   **reconciler** re-publishes stale `Pending` jobs every 30 s (an "outbox-lite").
2. The worker consumes the message (prefetch 1, manual ack) and **claims the job with a lease** in one conditional
   update (`status ∈ {Pending, Running}` and no live lease). The database row is the source of truth; the message is
   only a trigger.
3. Breadth-first by depth level: `Discovered` pages at the lowest depth are fetched through a sliding window of
   up to 4 concurrent requests; each page is parsed, links are normalized and deduplicated, the ratio is computed,
   and the page row, new child rows and edges are saved in one `SaveChanges`.
4. Counters on the job row are flushed every 5 pages (renewing the lease) and a `JobProgressChanged` event is
   published on the `crawl.events` fanout exchange. The API pushes it to the browser over SSE.
5. On completion the job becomes `Completed` (`Failed` if the root page could not be fetched, `Canceled` if the user
   canceled). Terminal transitions are conditional updates (`status = Running and lease owner = me`), so a cancel can
   never be overwritten.

### Crawling rules

- **Starting domain** = the exact host of the input URL, as the brief defines it (punycode, case-insensitive; ports
  and scheme ignored). So `blog.example.com` is *external* to `example.com`; if the registrable domain were intended
  instead, `DomainLinkRatio.IsInternal` is the single place to change and every ratio would move. If the *root*
  page redirects to another host (`example.com → www.example.com`) the starting domain follows the redirect —
  otherwise every link would be "external" and nothing could be crawled. Non-root redirects never change it.
- **URL normalization**: resolve relative to the page's *final* URL (honouring an http(s) `<base href>`), http(s)
  only, fragment removed, scheme/host lower-cased, host in punycode, default port and userinfo removed, embedded
  whitespace removed. Trailing slashes are kept (`/docs` and `/docs/` are different resources; servers that alias
  them answer with a redirect, which is recorded as `finalUrl`). `mailto:`, `tel:`, `javascript:`, `data:` are ignored;
  links to the page itself are not outgoing links.
- **Duplicates**: the same normalized URL is stored and fetched at most once per job; a page that redirects to an
  already-known URL is recorded as `Skipped`.
- **HTML only**: `Content-Type` is checked before the body is read; other content is `Skipped` without downloading
  it. Bodies are capped at 2 MB; charset comes from BOM → header → `<meta charset>` → UTF-8 (legacy code pages supported).
- **External links are counted, not crawled** (`Crawl:FollowExternalLinks=false`): they count toward the ratio but
  create no page rows — a depth-2 crawl of almost any site would otherwise be dominated by social-network home pages.
- **Depth and page budget**: pages *at* `maxDepth` are fetched (so their ratio is real) but their children are not
  stored; once a job knows `maxPages` URLs, further discovered links are counted but not stored. The page table is
  bounded by `maxPages` per job, not just the fetch count.
- **HTTP**: 10 s per attempt, 30 s total, up to 3 retries with jittered exponential backoff on 5xx/408/429/transport
  errors; the circuit breaker is scoped per host so one bad site cannot fail others; ≤ 5 redirects. A page that still
  fails is recorded as `Failed` with a sanitized reason; only an unreachable root fails the job.
- **SSRF guard**: the fetcher resolves each host and refuses loopback, private, link-local, CGNAT and metadata
  addresses at connect time, so redirects and DNS rebinding are covered. **To crawl a site on your own machine or
  LAN** (e.g. a local dev server), set `Crawl__AllowPrivateAddresses: "true"` on the worker in `docker-compose.yml`.

## Event-driven design

### Topology (declared idempotently by API and worker)

```
crawl (direct) ──crawl.job.requested──▶ crawl.jobs ──(nack, no requeue)──▶ crawl.dlx ──▶ crawl.jobs.dlq
                        ▲
crawl.jobs.retry ───────┘  (no consumer; per-message TTL; expired messages dead-letter back to crawl)

crawl.events (fanout) ──▶ one exclusive, auto-delete queue per API instance (SSE fan-out)
```

### Message schemas

`crawl.job.requested` (persistent, confirmed; `message-id = jobId`, `correlation-id`, headers `x-retry-count`, `x-correlation-id`):
```json
{ "jobId": "7f0f0a3e-…", "url": "https://example.com/", "maxDepth": 2, "maxPages": 200, "requestedAt": "2026-10-28T17:00:00Z" }
```
The job row is authoritative; the message fields are informational (useful when inspecting the DLQ).

`crawl.job.progress` on `crawl.events` (non-persistent, best-effort notification):
```json
{ "jobId": "7f0f0a3e-…", "status": "Running", "pagesCrawled": 35, "pagesDiscovered": 120, "occurredAt": "…" }
```
Enums are serialized as strings so the wire format survives enum changes.

### Idempotency (at-least-once delivery)

1. **Lease, not hope.** Claiming a job is one conditional update. A redelivered message for a job whose lease is
   still held by another worker is *deferred* (parked in the retry queue until the lease would expire) instead of
   crawled twice; a job whose worker died resumes once the lease expires. Terminal jobs are acknowledged and ignored.
2. **Resume from persisted state.** A `Running` job continues from the rows already in the database.
3. **Unique keys as the hard guarantee.** `pages (job_id, url)` and `edges (job_id, parent_page_id, child_page_id)`
   are unique; a unique violation during a save is treated as "already exists" and the page's links are re-applied.
4. **Conditional status transitions** everywhere (API cancel, worker finish/fail/cancel, reconciler), so the last
   writer never silently wins.

### Retry policy

| Layer | Policy | Transient = |
|-------|--------|-------------|
| HTTP (per page) | up to 3 retries, exponential backoff + jitter, 10 s/attempt, 30 s total, per-host circuit breaker | 5xx, 408, 429, transport errors, timeouts |
| Message (per job) | re-published to `crawl.jobs.retry` with TTL `5 s · 2^n` (n = 0..2); the broker returns it to `crawl.jobs` when the TTL expires — no consumer slot is blocked | database/broker connectivity (`NpgsqlException`, EF retry exhaustion, `TimeoutException`, `SocketException`, RabbitMQ client exceptions) |

The message-level decision (retry with `x-retry-count = n+1` / DLQ as non-transient / DLQ as exhausted) is a pure
function, `DeliveryFailurePolicy`, unit-tested separately from the RabbitMQ plumbing.
Non-transient page failures (404, non-HTML, blocked address) are recorded on the page row and never fail the job.
RabbitMQ only expires messages at the head of a queue; because delays only grow with the attempt number this
head-of-line effect is harmless here (one queue per delay tier is the next step if it ever matters).

### Dead-letter queue

A message goes to `crawl.jobs.dlq` when it cannot be deserialized (poison), when the job does not exist or another
non-transient error escapes the engine, or when transient retries are exhausted. The worker then marks the job
`Failed` with the reason (best effort). If that write itself fails, the **reconciler** later fails any `Running` job
whose lease expired more than 5 minutes ago, so nothing stays "live" forever. Replaying from the DLQ is manual
(RabbitMQ UI → move to `crawl`; reset `x-retry-count` first).

## UI

Three screens (React + Vite + TypeScript, plain CSS): **Start Crawl**, **Job Details** and **History**, with a
primary tab navigation. Job Details shows status, timestamps, a live progress bar, summary counters and the page
tree in two interchangeable views — an indented **list** (collapsible, filterable by URL and status, with the
domain-link ratio, HTTP code, title, redirect note and on-demand outgoing-link lists per page) and an SVG **graph**
(nodes and edges laid out by depth, collapsible, same filters). Both update while the crawl runs; errors, loading
and not-found states are explicit.

## Live updates (SSE)

Polling would be enough for this requirement; push updates are a bonus. Both exist: `GET /api/jobs/{id}/events` streams
the job state as `job` events — an immediate snapshot, then one per progress event (plus a 5 s safety refresh and
15 s keep-alives) and a final `end`. The worker's events are a *notification* channel: the API re-reads the row, so
a lost event costs at most one refresh. Each API instance has its own fanout queue, so SSE works behind a load
balancer. The UI falls back to 1.5 s polling if the stream fails and shows which mode is active.

## SQL model and performance

```
jobs  (Id uuid PK, InputUrl, StartingDomain, MaxDepth, MaxPages, Status, FailureReason,
       CreatedAt, StartedAt, CompletedAt, PagesCrawled, PagesDiscovered, LeaseOwner, LeaseExpiresAt)
       IX (CreatedAt DESC)   IX (Status, CreatedAt)           ← history; reconciler
pages (Id bigint PK, JobId FK, Url, FinalUrl, Depth, ParentPageId FK→pages, Status, HttpStatusCode, Title, Error,
       OutgoingLinkCount, InternalLinkCount, DomainLinkRatio, OutgoingLinks text[], CrawledAt)
       UX (JobId, Url) ← idempotency key    IX (JobId, Depth, Id) ← frontier + tree order, no sort
edges (Id bigint PK, JobId FK, ParentPageId FK, ChildPageId FK)
       UX (JobId, ParentPageId, ChildPageId) ← idempotency key
```

- History: backward range scan of the `CreatedAt` index + `COUNT(*)`, projected straight to DTOs, stable ordering
  (`CreatedAt, Id`), page bounds validated so OFFSET arithmetic cannot overflow.
- Tree: one projected query (`WHERE JobId ORDER BY Depth, Id`, bounded by `maxPages`), assembled in O(n). The
  per-page link lists (`text[]`, TOAST-stored) are **not** selected by the tree query; the links endpoint reads them.
- Metrics (counts, ratio) are computed once at crawl time; reads never re-aggregate edges.
- Writes: one `SaveChanges` per page, counters every 5 pages, change tracker cleared per batch; the edge table is
  the dominant write volume (~25 edges per page on link-dense sites) and is only read on resume.
- Response compression is enabled; the UI refetches the tree only when `pagesCrawled` or the status changed.

## Security notes

Crawling user-supplied URLs is an SSRF surface by definition, so: private/loopback/link-local/metadata destinations
are blocked at connect time; redirects are capped; bodies are capped; fetch errors are sanitized (details stay in
logs); job creation is rate-limited per client; CORS is limited to the dev origins (the SPA is same-origin behind
nginx); nginx sends `nosniff`, `X-Frame-Options`, `Referrer-Policy` and a CSP; the UI renders crawled text only
(never HTML) and only links `http(s)` URLs; containers run as non-root; infrastructure ports bind to loopback.
Out of scope: authentication/multi-tenancy, robots.txt, real secrets management (compose uses dev credentials).

## Observability

Serilog structured logs (compact JSON in containers, readable console in Development). Every worker line for a job
carries `JobId` and `CorrelationId`; the API logs each request with its correlation id. Health: `/health`
(liveness) and `/health/ready` on both services, wired as Docker healthchecks.

## What was built first, what was cut, what's next

**Time, honestly.** The first working version — schema + migrations, message contract and topology, the crawl
engine with normalization and the ratio, API and UI against a fixed contract, unit + fixture tests, compose, this
README — took about the suggested four hours. Idempotency (unique keys) and the DLQ were in that version. Everything
beyond the brief came from a second pass of reviewing that version against the requirements and running it against
real sites: the job lease and reconciler, the TTL retry queue, SSE, the SSRF guard, redirect/charset handling,
per-page link lists, the graph view and the API test project. They are additive; the four-hour core is the same.

**First (and why)** — the vertical slice, because every rubric item hangs off it and because the hard requirement
(idempotent at-least-once processing) is cheapest to get right in the schema before any code depends on it.

**Cut / simplified (deliberately)**
- No transactional outbox; the reconciler closes the same gaps with a 30 s delay instead of exactly-once plumbing.
- One job is processed by one worker (prefetch 1); compose runs two workers so jobs run in parallel across workers.
- No robots.txt / per-host politeness beyond a connection cap; no authentication.
- Frontend has no automated tests (type-checked, lint-clean; behaviour verified in the browser).

**Next**
1. Page-level fan-out (`crawl.page.requested`, shared frontier) so one large job spreads across workers.
2. Keyset pagination for history; ETag on the tree endpoint.
3. robots.txt + crawl-delay; content hashing to detect duplicate pages under different URLs.
4. Testcontainers-based end-to-end test (Postgres + RabbitMQ) in CI; a frontend component test suite.

## Known limitations / assumptions

- RabbitMQ's default `consumer_timeout` (30 min) bounds a single crawl; the page budget keeps crawls far below it.
- The SSRF guard does not translate NAT64 (`64:ff9b::/96`) or IPv4-compatible IPv6 addresses; the worker's network
  should not have NAT64 reachability to private ranges.
- Worker leases are renewed on every progress flush (and time-based while slow pages are in flight); a worker that
  stalls for longer than the lease (2 min) hands the job to the next delivery.
- `PagesDiscovered` counts stored URLs (≤ `maxPages`), which is what the progress bar divides by.
- A page redirecting to another host is stored under its requested URL with `finalUrl` recorded; its links are
  resolved against the final URL but it is still counted as a page of this job.
- Timestamps are UTC; the UI renders them in local time with the ISO value as a tooltip.

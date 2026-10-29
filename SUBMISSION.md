# Submission summary

**Run it:** `docker compose up --build` → UI http://localhost:3000, API http://localhost:8080/swagger.
**Tests:** `dotnet test backend/Crawler.sln` (531 tests) · `cd frontend && npm run lint && npm run build`.
Full documentation, architecture and trade-offs: [README.md](README.md).

## What was implemented

- **Two services over RabbitMQ.** `Crawler.Api` (.NET 8 minimal API) creates jobs, serves status / tree /
  per-page links / paginated history, cancels jobs and streams progress over SSE. `Crawler.Worker` (.NET 8)
  consumes one message per job, crawls breadth-first (HTML only, default depth 2, page budget 200, timeouts and
  retries), computes the Domain Link Ratio per page and persists pages and parent → child edges in PostgreSQL.
- **Event-driven robustness.** At-least-once delivery is handled by unique keys (`pages(job, url)`,
  `edges(job, parent, child)`), a per-job worker lease, resume from persisted state and conditional status
  transitions; transient failures go to a TTL retry queue, poison messages to a dead-letter queue; a reconciler
  re-publishes lost `Pending` jobs and fails jobs whose worker died.
- **React UI** (Vite + TypeScript): Start Crawl, Job Details (live progress, collapsible list and top-down graph
  views of the page tree with the ratio per page, on-demand outgoing-link lists, cancel / re-run), History
  (paginated, filterable). Loading, error and not-found states are explicit.
- **Engineering practices.** Domain / Infrastructure / Api / Worker layering with a pure domain; 531 tests
  (unit tests for URL normalization and the ratio, fixture-based integration tests of the crawl engine on SQLite,
  endpoint tests with `WebApplicationFactory`); structured Serilog logs with `JobId` and `CorrelationId`; health
  endpoints on both services; Docker Compose with health-ordered startup; GitHub Actions CI.

## Choices and trade-offs

| Choice | Why | Trade-off |
|---|---|---|
| One message per job, BFS inside the worker | Keeps "same URL once per job" an in-memory decision; makes redelivery trivially safe | One job is bound to one worker; scaling is across jobs (compose runs two workers) |
| Database row is the source of truth, message is a trigger | Duplicate deliveries cost nothing; resume is free | Needs a lease to stop two workers crawling one job |
| Reconciler instead of a transactional outbox | Closes the publish-after-commit gap in ~30 s with no extra table | Not exactly-once; a lost message is recovered late rather than never |
| TTL retry queue instead of in-process delay | Retries never block a consumer slot | RabbitMQ expires at the head of the queue (harmless with growing delays) |
| SSE with polling fallback | Live progress without a WebSocket dependency | Per-instance fanout queues; events are notifications, the API re-reads the row |
| External links counted but not crawled | A depth-2 crawl of any site would otherwise be dominated by social-network pages | Configurable (`Crawl:FollowExternalLinks`) |
| Page budget bounds stored rows, not just fetches | A large site exposed 2,600 URLs from 60 fetched pages | Pages past the budget are counted but not listed |
| Exact-host starting domain (as the brief defines it) | Matches the specification literally | `blog.example.com` is external to `example.com` |

## What was prioritized

1. The vertical slice with idempotency designed into the schema (correctness and event-driven robustness carry
   most of the weight and are cheapest to get right early).
2. Running against real sites and fixing what that exposed: redirect handling, charsets, the page-table bound,
   lease and reconciler, SSRF guard.
3. Tests and documentation proportional to the behaviour that matters (idempotency, retries, DLQ, normalization).

Cut deliberately: transactional outbox, page-level fan-out across workers, robots.txt / politeness, authentication,
frontend unit tests. Next steps are listed in the README.

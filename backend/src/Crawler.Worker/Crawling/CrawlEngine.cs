using System.Data.Common;
using Crawler.Domain.Crawling;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog.Context;

namespace Crawler.Worker.Crawling;

/// <summary>
/// Breadth-first crawl of one job. All state lives in the database, so a redelivered message for a
/// Running job resumes from the persisted frontier instead of starting over.
///
/// Concurrency: the job is claimed with a lease (<see cref="CrawlJob.LeaseOwner"/>/<see cref="CrawlJob.LeaseExpiresAt"/>).
/// The job row is never tracked; every write to it is a conditional update filtered by this worker's lease, so a
/// concurrent cancel (API) or a second worker can never be overwritten.
/// </summary>
public sealed class CrawlEngine
{
    private const int MaxUrlLength = 2048;
    private const int MaxTitleLength = 512;
    private const int MaxErrorLength = 1024;
    private const string UnexpectedError = "Unexpected error";
    private const string RedirectAliasError = "Redirects to an already known page";

    private readonly CrawlerDbContext _db;
    private readonly IPageFetcher _fetcher;
    private readonly CrawlOptions _options;
    private readonly ILogger<CrawlEngine> _logger;
    private readonly TimeProvider _time;
    private readonly IJobProgressNotifier? _notifier;
    private readonly string _workerId;

    public CrawlEngine(
        CrawlerDbContext db,
        IPageFetcher fetcher,
        IOptions<CrawlOptions> options,
        ILogger<CrawlEngine> logger,
        TimeProvider? timeProvider = null,
        IJobProgressNotifier? notifier = null,
        WorkerIdentity? workerIdentity = null)
    {
        _db = db;
        _fetcher = fetcher;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _notifier = notifier;
        _workerId = (workerIdentity ?? new WorkerIdentity()).Id;
    }

    /// <exception cref="JobNotFoundException">The job does not exist.</exception>
    /// <exception cref="JobLeaseHeldException">Another worker holds a live lease on the job.</exception>
    public async Task RunAsync(CrawlJobRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using (LogContext.PushProperty("JobId", message.JobId))
        {
            var job = await LoadJobAsync(message.JobId, cancellationToken) ?? throw new JobNotFoundException(message.JobId);
            if (job.IsTerminal)
            {
                _logger.LogInformation("Job is already {Status}; ignoring redelivered message", job.Status);
                return;
            }

            var rootUrl = ResolveRootUrl(message, job);
            var run = await StartAsync(job, rootUrl, cancellationToken);
            if (run is null) return;

            try
            {
                await CrawlAsync(run, cancellationToken);
            }
            catch
            {
                await TryReleaseLeaseAsync(run);
                throw;
            }
        }
    }

    private async Task CrawlAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        if (run.RootPage.Status is PageStatus.Failed or PageStatus.Skipped)
        {
            await FailJobAsync(run, run.RootPage, cancellationToken);
            return;
        }

        var outcome = await CrawlFrontierAsync(run, cancellationToken);
        await FinishAsync(run, outcome, cancellationToken);
    }

    // ---------------------------------------------------------------- start / resume

    private Task<CrawlJob?> LoadJobAsync(Guid jobId, CancellationToken cancellationToken)
        => _db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    /// <summary>The database row is the source of truth; the message URL is only a fallback.</summary>
    private static string ResolveRootUrl(CrawlJobRequested message, CrawlJob job)
    {
        if (UrlNormalizer.TryNormalize(job.InputUrl, null, out var uri) || UrlNormalizer.TryNormalize(message.Url, null, out uri))
            return uri.AbsoluteUri;

        throw new ArgumentException($"Job {job.Id} has no valid absolute http(s) URL ('{job.InputUrl}').", nameof(message));
    }

    /// <summary>Claims the job and loads the persisted graph. Returns null when the job turned out to be terminal.</summary>
    private async Task<CrawlRun?> StartAsync(CrawlJob snapshot, string rootUrl, CancellationToken cancellationToken)
    {
        var rootHost = UrlNormalizer.CanonicalHost(new Uri(rootUrl));
        if (!await TryClaimAsync(snapshot.Id, rootHost, cancellationToken))
            return await OnClaimRejectedAsync(snapshot.Id, cancellationToken);

        var job = await LoadJobAsync(snapshot.Id, cancellationToken) ?? throw new JobNotFoundException(snapshot.Id);
        var run = new CrawlRun(job.Id, job.StartingDomain, EffectiveMaxDepth(job), EffectiveMaxPages(job)) { LeaseRenewAt = NextLeaseRenewal() };
        await ReloadKnownGraphAsync(run, cancellationToken);
        run.Processed = await _db.Pages.CountAsync(p => p.JobId == job.Id && p.Status != PageStatus.Discovered, cancellationToken);
        run.RootPage = await EnsureRootPageAsync(run, rootUrl, cancellationToken);

        var resuming = snapshot.Status == JobStatus.Running;
        if (!resuming) await NotifyAsync(run, JobStatus.Running, cancellationToken);
        _logger.LogInformation(
            resuming
                ? "Resuming crawl of {RootUrl} (MaxDepth {MaxDepth}, MaxPages {MaxPages}, {Processed} pages already processed)"
                : "Starting crawl of {RootUrl} (MaxDepth {MaxDepth}, MaxPages {MaxPages}, {Processed} pages already processed)",
            rootUrl, run.MaxDepth, run.MaxPages, run.Processed);
        return run;
    }

    /// <summary>
    /// One atomic statement: a non-terminal job whose lease is free, expired or already ours (a redelivery to
    /// the same worker after a failed attempt) becomes Running under this worker's lease.
    /// </summary>
    private async Task<bool> TryClaimAsync(Guid jobId, string rootHost, CancellationToken cancellationToken)
    {
        var now = Now;
        var expires = now + LeaseDuration;
        var owner = _workerId;
        var claimed = await _db.Jobs
            .Where(j => j.Id == jobId
                        && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
                        && (j.LeaseExpiresAt == null || j.LeaseExpiresAt < now || j.LeaseOwner == owner))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseOwner, owner)
                .SetProperty(j => j.LeaseExpiresAt, expires)
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                .SetProperty(j => j.StartingDomain, j => j.StartingDomain == "" ? rootHost : j.StartingDomain),
                cancellationToken);
        return claimed == 1;
    }

    private async Task<CrawlRun?> OnClaimRejectedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var current = await LoadJobAsync(jobId, cancellationToken) ?? throw new JobNotFoundException(jobId);
        if (current.IsTerminal)
        {
            _logger.LogInformation("Job became {Status} before it could be claimed; ignoring message", current.Status);
            return null;
        }

        var retryAfter = (current.LeaseExpiresAt ?? Now) - Now;
        throw new JobLeaseHeldException(jobId, retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
    }

    private int EffectiveMaxDepth(CrawlJob job) => Math.Clamp(job.MaxDepth, 0, _options.MaxDepthCap);

    private int EffectiveMaxPages(CrawlJob job) => job.MaxPages > 0 ? job.MaxPages : _options.DefaultMaxPages;

    private async Task<CrawlPage> EnsureRootPageAsync(CrawlRun run, string rootUrl, CancellationToken cancellationToken)
    {
        if (run.KnownPageIds.TryGetValue(rootUrl, out var rootId))
            return await _db.Pages.SingleAsync(p => p.Id == rootId, cancellationToken);

        var root = new CrawlPage { JobId = run.JobId, Url = rootUrl, Depth = 0, ParentPageId = null, Status = PageStatus.Discovered };
        _db.Pages.Add(root);
        await _db.SaveChangesAsync(cancellationToken);
        run.KnownPageIds[rootUrl] = root.Id;
        return root;
    }

    /// <summary>
    /// In-memory idempotency caches: every URL already stored for the job, every stored redirect target
    /// (FinalUrl → page) and every stored parent→child edge.
    /// The unique indexes in the database remain the safety net (see <see cref="SaveTolerantOfDuplicatesAsync"/>).
    /// </summary>
    private async Task ReloadKnownGraphAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        var jobId = run.JobId;
        var pages = await _db.Pages.AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => new { p.Url, p.Id, p.FinalUrl })
            .ToListAsync(cancellationToken);
        run.KnownPageIds = pages.ToDictionary(p => p.Url, p => p.Id, StringComparer.Ordinal);
        run.AliasToPageId = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var page in pages.Where(p => p.FinalUrl is not null).OrderBy(p => p.Id))
            RememberAlias(run, page.FinalUrl!, page.Id);

        var edges = await _db.Edges.AsNoTracking()
            .Where(e => e.JobId == jobId)
            .Select(e => new { e.ParentPageId, e.ChildPageId })
            .ToListAsync(cancellationToken);
        run.KnownEdges = edges.Select(e => (e.ParentPageId, e.ChildPageId)).ToHashSet();
    }

    // ---------------------------------------------------------------- BFS loop

    private async Task<CrawlOutcome> CrawlFrontierAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        while (run.Processed < run.MaxPages)
        {
            var batch = await NextFrontierBatchAsync(run, cancellationToken);
            if (batch.Count == 0) return CrawlOutcome.FrontierExhausted;

            var outcome = await ProcessBatchAsync(run, batch, cancellationToken);
            if (outcome is not null) return outcome.Value;

            // Keeps the change tracker small on large crawls; the job row is never tracked.
            _db.ChangeTracker.Clear();
        }

        return CrawlOutcome.PageBudgetReached;
    }

    /// <summary>
    /// Sliding window: up to <see cref="CrawlOptions.MaxConcurrentFetches"/> fetch+parse tasks run at once and each
    /// result is persisted as soon as it arrives (sequentially — one DbContext, which is not thread-safe). Parsing is
    /// CPU-bound, so it runs on the thread pool inside the window (at most MaxConcurrentFetches documents parsed at
    /// once); the persistence loop only applies the run-dependent rules and writes. Returns an outcome to stop the
    /// crawl, or null. Stopping early leaves the remaining pages Discovered; their in-flight tasks are cancelled and awaited.
    /// </summary>
    private async Task<CrawlOutcome?> ProcessBatchAsync(CrawlRun run, List<CrawlPage> batch, CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(_options.MaxConcurrentFetches);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = batch.Select(page => FetchThrottledAsync(page, throttle, stop.Token)).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var fetched = await done;

                var outcome = await HandleFetchedPageAsync(run, fetched, cancellationToken);
                if (outcome is not null) return outcome;
            }
            return null;
        }
        finally
        {
            await stop.CancelAsync();
            await DrainAsync(pending);
        }
    }

    /// <summary>
    /// Runs concurrently with other pages and with persistence: it must not touch the DbContext, the run caches or
    /// any mutable state of <paramref name="page"/> (only its immutable Url is read).
    /// </summary>
    private async Task<FetchedPage> FetchThrottledAsync(CrawlPage page, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            var url = page.Url;
            var result = await FetchSafelyAsync(url, cancellationToken);
            // Task.Run guarantees the parse leaves the persistence loop even when a fetch completed synchronously.
            var document = result.Succeeded
                ? await Task.Run(() => ParsedDocument.Parse(url, result), cancellationToken)
                : null;
            return new FetchedPage(page, result, document);
        }
        finally
        {
            throttle.Release();
        }
    }

    private static async Task DrainAsync(List<Task<FetchedPage>> pending)
    {
        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
            // Expected: the window was cancelled.
        }
    }

    private async Task<CrawlOutcome?> HandleFetchedPageAsync(CrawlRun run, FetchedPage fetched, CancellationToken cancellationToken)
    {
        var page = fetched.Page;
        await PersistPageAsync(run, fetched, cancellationToken);
        run.Processed++;
        if (run.LeaseLost) return CrawlOutcome.LeaseLost;

        if (page.Id == run.RootPage.Id)
        {
            run.RootPage = page;
            if (page.Status != PageStatus.Crawled) return CrawlOutcome.RootFailed;
        }

        // Time-based renewal too: a few slow pages must not let the lease expire between page-count flushes.
        // A successful flush also proves the job is still Running, so it doubles as the cancellation check.
        if (IsDue(run.Processed, _options.ProgressFlushEveryPages) || Now >= run.LeaseRenewAt)
        {
            if (await FlushProgressAsync(run, cancellationToken) is { } stop) return stop;
        }
        else if (IsDue(run.Processed, _options.CancellationCheckEveryPages) && await IsCanceledAsync(run.JobId, cancellationToken))
        {
            return CrawlOutcome.Canceled;
        }

        return run.Processed >= run.MaxPages ? CrawlOutcome.PageBudgetReached : null;
    }

    private static bool IsDue(int processed, int every) => every <= 1 || processed % every == 0;

    /// <summary>
    /// Next pages to fetch in BFS order (Depth, then Id). A batch never spans two depths so that a level is
    /// fully processed before its children, and never exceeds the remaining page budget.
    /// </summary>
    private async Task<List<CrawlPage>> NextFrontierBatchAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, Math.Min(_options.MaxConcurrentFetches * 4, run.MaxPages - run.Processed));
        var jobId = run.JobId;
        var maxDepth = run.MaxDepth;

        var candidates = await _db.Pages
            .Where(p => p.JobId == jobId && p.Status == PageStatus.Discovered && p.Depth <= maxDepth)
            .OrderBy(p => p.Depth).ThenBy(p => p.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0) return candidates;
        var depth = candidates[0].Depth;
        return candidates.TakeWhile(p => p.Depth == depth).ToList();
    }

    private async Task<FetchResult> FetchSafelyAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _fetcher.FetchAsync(new Uri(url), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The fetcher contract says it never throws; this keeps one misbehaving page from failing the whole job.
            _logger.LogWarning(ex, "Unexpected error fetching {Url}", url);
            return FetchResult.Failed(null, UnexpectedError);
        }
    }

    // ---------------------------------------------------------------- per-page persistence

    private async Task PersistPageAsync(CrawlRun run, FetchedPage fetched, CancellationToken cancellationToken)
    {
        var (page, result, document) = fetched;
        page.HttpStatusCode = result.StatusCode;
        page.CrawledAt = Now;
        var startingDomain = run.StartingDomain;

        var linksToStore = document is not null ? ApplyHtmlSafely(run, page, document) : MarkUnsuccessful(page, result);
        if (run.StartingDomain != startingDomain) await SaveStartingDomainAsync(run, cancellationToken);
        // Before AddLinks: a link to this page's own final URL must resolve to this page (self-edge guard).
        if (page.FinalUrl is not null) RememberAlias(run, page.FinalUrl, page.Id);

        var newChildren = linksToStore is null ? [] : AddLinks(run, page, linksToStore);
        newChildren = await SaveTolerantOfDuplicatesAsync(run, page, linksToStore, newChildren, cancellationToken);
        RememberSavedChildren(run, page, newChildren);

        _logger.LogDebug("Processed {Url} (depth {Depth}): {Status}, {LinkCount} links, {NewPages} new pages",
            page.Url, page.Depth, page.Status, page.OutgoingLinkCount, newChildren.Count);
    }

    private static IReadOnlyList<Uri>? MarkUnsuccessful(CrawlPage page, FetchResult result)
    {
        page.Status = result.IsNotHtml ? PageStatus.Skipped : PageStatus.Failed;
        page.Error = Truncate(result.Error ?? UnexpectedError, MaxErrorLength);
        return null;
    }

    /// <summary>
    /// Applies an already parsed HTML page (redirect rules, metrics). Returns the links whose pages/edges should be
    /// stored, or null. Any exception — here or while parsing — only fails this page; one malformed document must
    /// never dead-letter the whole job.
    /// </summary>
    private IReadOnlyList<Uri>? ApplyHtmlSafely(CrawlRun run, CrawlPage page, ParsedDocument document)
    {
        if (document.Error is not null) return FailDocument(page, document.Error);
        try
        {
            var finalUrl = document.FinalUrl;
            var isRoot = page.Id == run.RootPage.Id;
            if (!isRoot && IsRedirectAlias(run, page, finalUrl))
            {
                page.Status = PageStatus.Skipped;
                page.Error = RedirectAliasError;
                return null;
            }

            page.FinalUrl = finalUrl is not null && finalUrl.AbsoluteUri != page.Url && finalUrl.AbsoluteUri.Length <= MaxUrlLength
                ? finalUrl.AbsoluteUri
                : null;
            if (isRoot && finalUrl is not null) AdoptRootRedirectHost(run, finalUrl);

            // Metrics depend on the starting domain, which the root redirect rule above may just have changed.
            var parsed = document.Page!;
            ApplyMetrics(run, page, parsed);

            // Depth rule: pages at MaxDepth are fetched and their links counted (for the ratio),
            // but their children are not stored — they could never be crawled and would bloat the page table.
            return page.Depth < run.MaxDepth ? parsed.Links : null;
        }
        catch (Exception ex)
        {
            return FailDocument(page, ex);
        }
    }

    private IReadOnlyList<Uri>? FailDocument(CrawlPage page, Exception ex)
    {
        _logger.LogWarning(ex, "Could not process the document of {Url}", page.Url);
        ResetMetrics(page);
        page.Status = PageStatus.Failed;
        page.Error = UnexpectedError;
        return null;
    }

    private static Uri? NormalizeFinalUrl(Uri? finalUrl)
        => finalUrl is not null && UrlNormalizer.TryNormalize(finalUrl.AbsoluteUri, null, out var normalized) ? normalized : null;

    /// <summary>
    /// The page redirected to a URL that is already another page of this job (its URL or another page's final
    /// URL): crawling it would duplicate content.
    /// </summary>
    private static bool IsRedirectAlias(CrawlRun run, CrawlPage page, Uri? finalUrl)
        => finalUrl is not null
           && finalUrl.AbsoluteUri != page.Url
           && run.TryResolve(finalUrl.AbsoluteUri, out var knownId)
           && knownId != page.Id;

    /// <summary>
    /// Records that <paramref name="finalUrl"/> (already normalized) is served by page <paramref name="pageId"/>, so
    /// later links to it become edges to that page instead of a second row and fetch. A URL that is itself a page
    /// row keeps resolving to that row; the first page to claim a final URL wins.
    /// </summary>
    private static void RememberAlias(CrawlRun run, string finalUrl, long pageId)
    {
        if (!run.KnownPageIds.ContainsKey(finalUrl)) run.AliasToPageId.TryAdd(finalUrl, pageId);
    }

    private void ApplyMetrics(CrawlRun run, CrawlPage page, ParsedPage parsed)
    {
        page.Status = PageStatus.Crawled;
        page.Error = null;
        page.Title = Truncate(TextLimits.Sanitize(parsed.Title), MaxTitleLength);
        page.OutgoingLinkCount = parsed.Links.Count;
        page.InternalLinkCount = DomainLinkRatio.CountInternalTo(parsed.Links, run.CanonicalStartingDomain);
        page.DomainLinkRatio = DomainLinkRatio.Compute(page.OutgoingLinkCount, page.InternalLinkCount);
        page.OutgoingLinks = parsed.Links.Take(_options.MaxStoredLinksPerPage).Select(l => l.AbsoluteUri).ToList();
    }

    private static void ResetMetrics(CrawlPage page)
    {
        page.FinalUrl = null;
        page.Title = null;
        page.OutgoingLinkCount = 0;
        page.InternalLinkCount = 0;
        page.DomainLinkRatio = 0;
        page.OutgoingLinks = [];
    }

    /// <summary>
    /// Root redirect rule: when the root page redirects to another host (typically example.com → www.example.com),
    /// the final host becomes the job's starting domain. Otherwise every link on the site would count as external,
    /// the ratios would all be 0 and the crawl would stop after one page. Only the root can change the domain;
    /// a redirect on any other page never does. The new domain is persisted right after (lease-conditional).
    /// </summary>
    private void AdoptRootRedirectHost(CrawlRun run, Uri finalUrl)
    {
        var host = UrlNormalizer.CanonicalHost(finalUrl);
        if (string.Equals(host, run.StartingDomain, StringComparison.OrdinalIgnoreCase)) return;

        run.StartingDomain = host;
        _logger.LogWarning("Root redirected to {Host}; starting domain updated", host);
    }

    /// <summary>
    /// Stages a page row for every unseen URL (first parent wins → BFS tree) and an edge for every
    /// parent→child pair not stored yet (→ full link graph), including edges to already-known pages and to the page
    /// a URL is known to redirect from (e.g. a link to /docs/ after /docs redirected there).
    /// External links are skipped unless <see cref="CrawlOptions.FollowExternalLinks"/> is set.
    /// Returns the newly staged child pages.
    /// </summary>
    private List<CrawlPage> AddLinks(CrawlRun run, CrawlPage parent, IReadOnlyList<Uri> links)
    {
        var newChildren = new List<CrawlPage>();
        foreach (var link in links)
        {
            var url = link.AbsoluteUri;
            if (url.Length > MaxUrlLength) continue;
            if (!_options.FollowExternalLinks && !DomainLinkRatio.IsInternalTo(link, run.CanonicalStartingDomain)) continue;

            if (run.TryResolve(url, out var childId))
            {
                if (childId != parent.Id && run.KnownEdges.Add((parent.Id, childId)))
                    _db.Edges.Add(new CrawlEdge { JobId = run.JobId, ParentPageId = parent.Id, ChildPageId = childId });
                continue;
            }

            // Page budget bounds the table, not just the fetches: a URL discovered past MaxPages could never be
            // crawled, and on large sites a single level can expose thousands of them. Link counts and the
            // ratio are computed from the parsed links, so they stay exact.
            if (run.KnownPageIds.Count + newChildren.Count >= run.MaxPages) continue;

            var child = new CrawlPage
            {
                JobId = run.JobId,
                Url = url,
                Depth = parent.Depth + 1,
                ParentPageId = parent.Id,
                Status = PageStatus.Discovered
            };
            _db.Pages.Add(child);
            _db.Edges.Add(new CrawlEdge { JobId = run.JobId, ParentPageId = parent.Id, ChildPage = child });
            newChildren.Add(child);
        }
        return newChildren;
    }

    private static void RememberSavedChildren(CrawlRun run, CrawlPage parent, IEnumerable<CrawlPage> newChildren)
    {
        foreach (var child in newChildren.Where(c => c.Id != 0))
        {
            run.KnownPageIds[child.Url] = child.Id;
            run.KnownEdges.Add((parent.Id, child.Id));
        }
    }

    /// <summary>
    /// Idempotency safety net: if an insert hits a unique index (e.g. a concurrent duplicate delivery of the
    /// same job), drop the staged inserts, save the page itself, resync the in-memory caches and stage the page's
    /// links once more against the fresh caches, so non-conflicting children and edges are not lost.
    /// Any other database error propagates (transient ones are retried by the consumer).
    /// Returns the children that were actually inserted.
    /// </summary>
    private async Task<List<CrawlPage>> SaveTolerantOfDuplicatesAsync(
        CrawlRun run, CrawlPage page, IReadOnlyList<Uri>? links, List<CrawlPage> newChildren, CancellationToken cancellationToken)
    {
        if (await TrySaveAsync(run, cancellationToken)) return newChildren;
        if (links is null) return [];

        var retried = AddLinks(run, page, links);
        return await TrySaveAsync(run, cancellationToken) ? retried : [];
    }

    private async Task<bool> TrySaveAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var staged = _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList();
            _logger.LogWarning(ex, "Duplicate rows detected while saving; discarding {Count} staged inserts and resyncing", staged.Count);
            foreach (var entry in staged) entry.State = EntityState.Detached;

            await _db.SaveChangesAsync(cancellationToken);
            await ReloadKnownGraphAsync(run, cancellationToken);
            return false;
        }
    }

    internal static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
        // Microsoft.Data.Sqlite (tests): SQLITE_CONSTRAINT (19) also covers foreign keys, hence the message check.
        DbException db when db.GetType().Name == "SqliteException" => db.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal),
        _ => false
    };

    // ---------------------------------------------------------------- job writes (all conditional on the lease)

    private IQueryable<CrawlJob> OwnedJob(CrawlRun run)
    {
        var jobId = run.JobId;
        var owner = _workerId;
        return _db.Jobs.Where(j => j.Id == jobId && j.LeaseOwner == owner);
    }

    /// <summary>
    /// Persists the counters and extends the lease while the job is still Running under our lease. Returns null on
    /// success, otherwise why the crawl must stop: Canceled (by the API) or LeaseLost (taken over by another worker).
    /// </summary>
    private async Task<CrawlOutcome?> FlushProgressAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        var crawled = run.Processed;
        var discovered = run.KnownPageIds.Count;
        var expires = Now + LeaseDuration;
        var updated = await OwnedJob(run)
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.PagesCrawled, crawled)
                .SetProperty(j => j.PagesDiscovered, discovered)
                .SetProperty(j => j.LeaseExpiresAt, expires), cancellationToken);

        if (updated == 0)
        {
            if (await IsCanceledAsync(run.JobId, cancellationToken)) return CrawlOutcome.Canceled;
            OnLeaseLost(run);
            return CrawlOutcome.LeaseLost;
        }

        run.LeaseRenewAt = NextLeaseRenewal();
        await NotifyAsync(run, JobStatus.Running, cancellationToken);
        return null;
    }

    private async Task SaveStartingDomainAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        var domain = run.StartingDomain;
        var updated = await OwnedJob(run).ExecuteUpdateAsync(s => s.SetProperty(j => j.StartingDomain, domain), cancellationToken);
        if (updated == 0) OnLeaseLost(run);
    }

    private void OnLeaseLost(CrawlRun run)
    {
        run.LeaseLost = true;
        _logger.LogWarning("Lease on the job was lost; stopping without changing its status");
    }

    private Task<bool> IsCanceledAsync(Guid jobId, CancellationToken cancellationToken)
        => _db.Jobs.AsNoTracking().AnyAsync(j => j.Id == jobId && j.Status == JobStatus.Canceled, cancellationToken);

    private async Task FinishAsync(CrawlRun run, CrawlOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case CrawlOutcome.RootFailed:
                await FailJobAsync(run, run.RootPage, cancellationToken);
                return;
            case CrawlOutcome.Canceled:
                await MarkCanceledAsync(run, cancellationToken);
                return;
            case CrawlOutcome.LeaseLost:
                return;
        }

        // Conditional on Status == Running: a cancel that lands after the last periodic check is never overwritten.
        if (!await TryEndJobAsync(run, JobStatus.Completed, null, cancellationToken))
        {
            await MarkCanceledAsync(run, cancellationToken);
            return;
        }

        _logger.LogInformation("Crawl completed ({Reason}): {PagesCrawled} pages processed, {PagesDiscovered} discovered",
            outcome, run.Processed, run.KnownPageIds.Count);
    }

    /// <summary>Settles a job the API canceled: final counters, lease released. No-op when the lease is gone.</summary>
    private async Task MarkCanceledAsync(CrawlRun run, CancellationToken cancellationToken)
    {
        if (await TryEndJobAsync(run, JobStatus.Canceled, null, cancellationToken))
            _logger.LogInformation("Crawl canceled after {PagesCrawled} pages", run.Processed);
        else
            OnLeaseLost(run);
    }

    private async Task FailJobAsync(CrawlRun run, CrawlPage rootPage, CancellationToken cancellationToken)
    {
        var reason = Truncate($"Root page could not be fetched: {rootPage.Error}", MaxErrorLength)!;
        if (await TryEndJobAsync(run, JobStatus.Failed, reason, cancellationToken))
            _logger.LogWarning("Crawl failed: {FailureReason}", reason);
        else
            await MarkCanceledAsync(run, cancellationToken);
    }

    /// <summary>
    /// Terminal transition, conditional on our lease and on Status == Running (Canceled → Canceled is allowed so a
    /// cancel can be settled with final counters). Clears the lease. Notifies only when a row was updated.
    /// </summary>
    private async Task<bool> TryEndJobAsync(CrawlRun run, JobStatus status, string? failureReason, CancellationToken cancellationToken)
    {
        var now = Now;
        var crawled = run.Processed;
        var discovered = run.KnownPageIds.Count;
        var settlingCancel = status == JobStatus.Canceled;
        var updated = await OwnedJob(run)
            .Where(j => j.Status == JobStatus.Running || (settlingCancel && j.Status == JobStatus.Canceled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.FailureReason, j => failureReason ?? j.FailureReason)
                .SetProperty(j => j.CompletedAt, j => j.CompletedAt ?? now)
                .SetProperty(j => j.PagesCrawled, crawled)
                .SetProperty(j => j.PagesDiscovered, discovered)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null), cancellationToken);

        if (updated == 0) return false;
        await NotifyAsync(run, status, cancellationToken);
        return true;
    }

    /// <summary>Best effort after a failed attempt, so another worker can resume immediately instead of waiting for expiry.</summary>
    private async Task TryReleaseLeaseAsync(CrawlRun run)
    {
        try
        {
            await OwnedJob(run).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not release the job lease; it will expire on its own");
        }
    }

    /// <summary>Publishes the values just written; call only after the database update.</summary>
    private Task NotifyAsync(CrawlRun run, JobStatus status, CancellationToken cancellationToken)
        => _notifier is null
            ? Task.CompletedTask
            : _notifier.NotifyAsync(
                new JobProgressChanged(run.JobId, status, run.Processed, run.KnownPageIds.Count, Now),
                cancellationToken);

    // ---------------------------------------------------------------- helpers

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(_options.LeaseSeconds);

    private DateTime NextLeaseRenewal() => Now + LeaseDuration / 3;

    private static string? Truncate(string? value, int maxLength) => TextLimits.Truncate(value, maxLength);

    private enum CrawlOutcome
    {
        FrontierExhausted,
        PageBudgetReached,
        RootFailed,
        Canceled,
        LeaseLost
    }

    private sealed record FetchedPage(CrawlPage Page, FetchResult Result, ParsedDocument? Document);

    /// <summary>
    /// One fetched HTML document parsed off the persistence path. Depends only on the response, never on run state,
    /// so it is safe to compute concurrently; each HtmlDocument stays local to one <see cref="LinkExtractor.Parse"/> call.
    /// </summary>
    private sealed record ParsedDocument(Uri? FinalUrl, ParsedPage? Page, Exception? Error)
    {
        public static ParsedDocument Parse(string pageUrl, FetchResult result)
        {
            try
            {
                var finalUrl = NormalizeFinalUrl(result.FinalUrl);
                // The raw final URL keeps its exact path (e.g. a trailing slash), which relative links depend on.
                var baseUri = finalUrl is not null ? result.FinalUrl! : new Uri(pageUrl);
                return new ParsedDocument(finalUrl, LinkExtractor.Parse(result.Html!, baseUri), null);
            }
            catch (Exception ex)
            {
                return new ParsedDocument(null, null, ex);
            }
        }
    }

    /// <summary>Mutable state of one RunAsync invocation.</summary>
    private sealed class CrawlRun
    {
        public CrawlRun(Guid jobId, string startingDomain, int maxDepth, int maxPages)
        {
            JobId = jobId;
            StartingDomain = startingDomain;
            MaxDepth = maxDepth;
            MaxPages = maxPages;
        }

        public Guid JobId { get; }
        /// <summary>In-memory copy of the job's starting domain (may change once, by the root redirect rule).</summary>
        public string StartingDomain
        {
            get => _startingDomain;
            set
            {
                _startingDomain = value;
                CanonicalStartingDomain = DomainLinkRatio.Canonicalize(value);
            }
        }
        private string _startingDomain = null!;
        /// <summary><see cref="StartingDomain"/> canonicalised once, for the per-link internal check.</summary>
        public string CanonicalStartingDomain { get; private set; } = null!;
        public int MaxDepth { get; }
        public int MaxPages { get; }
        public CrawlPage RootPage { get; set; } = null!;
        /// <summary>Pages already fetched (Crawled, Failed or Skipped).</summary>
        public int Processed { get; set; }
        public DateTime LeaseRenewAt { get; set; }
        public bool LeaseLost { get; set; }
        public Dictionary<string, long> KnownPageIds { get; set; } = new(StringComparer.Ordinal);
        /// <summary>
        /// Normalized FinalUrl → id of the page that redirected there, for final URLs that are not page rows
        /// themselves. Not counted as discovered pages.
        /// </summary>
        public Dictionary<string, long> AliasToPageId { get; set; } = new(StringComparer.Ordinal);
        public HashSet<(long ParentId, long ChildId)> KnownEdges { get; set; } = [];

        /// <summary>The page a URL belongs to: its own row, else the page that redirected to it.</summary>
        public bool TryResolve(string url, out long pageId)
            => KnownPageIds.TryGetValue(url, out pageId) || AliasToPageId.TryGetValue(url, out pageId);
    }
}

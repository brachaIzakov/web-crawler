using Crawler.Api.Contracts;
using Crawler.Domain.Crawling;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Services;

public sealed class JobService : IJobService
{
    private readonly CrawlerDbContext _db;
    private readonly IJobPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<JobService> _logger;

    public JobService(CrawlerDbContext db, IJobPublisher publisher, TimeProvider time, ILogger<JobService> logger)
    {
        _db = db;
        _publisher = publisher;
        _time = time;
        _logger = logger;
    }

    public async Task<CreateJobResult> CreateAsync(ValidatedJobRequest request, string correlationId, CancellationToken ct)
    {
        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            InputUrl = request.Url.AbsoluteUri,
            StartingDomain = UrlNormalizer.CanonicalHost(request.Url),
            MaxDepth = request.MaxDepth,
            MaxPages = request.MaxPages,
            Status = JobStatus.Pending,
            CreatedAt = Now
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);

        // DB write and publish are not atomic. A job left Pending because the publish failed is picked up by
        // JobReconciler (outbox-lite), so the client still gets its job id; a full transactional outbox would
        // remove even that delay.
        try
        {
            var message = new CrawlJobRequested(job.Id, job.InputUrl, job.MaxDepth, job.MaxPages, Now);
            await _publisher.PublishAsync(message, correlationId, ct);
            _logger.LogInformation("Job {JobId} created for {Url}", job.Id, job.InputUrl);
            return new CreateJobResult(job.Id, CreateJobOutcome.Enqueued);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to enqueue job {JobId}; the reconciler will retry the publish", job.Id);
            return new CreateJobResult(job.Id, CreateJobOutcome.EnqueueDeferred);
        }
    }

    public async Task<PagedResult<JobSummary>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var totalCount = await _db.Jobs.CountAsync(ct);

        var items = await _db.Jobs
            .AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new JobSummary(
                j.Id, j.InputUrl, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt,
                j.PagesCrawled, j.PagesDiscovered, j.MaxDepth))
            .ToListAsync(ct);

        return new PagedResult<JobSummary>(items, page, pageSize, totalCount);
    }

    public async Task<JobDetails?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        return job is null ? null : ToDetails(job);
    }

    public async Task<JobTreeResponse?> GetTreeAsync(Guid jobId, CancellationToken ct)
    {
        // Projected: the per-page link arrays stay in the database (they are served by GetPageLinksAsync).
        var rows = await _db.Pages
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .OrderBy(p => p.Depth)
            .ThenBy(p => p.Id)
            .Select(p => new TreePageRow(
                p.Id, p.ParentPageId, p.Url, p.FinalUrl, p.Depth, p.Status, p.HttpStatusCode, p.Title,
                p.OutgoingLinkCount, p.InternalLinkCount, p.DomainLinkRatio, p.Error))
            .ToListAsync(ct);

        // Pages imply the job exists (FK); only an empty result needs the extra existence check.
        if (rows.Count == 0 && !await _db.Jobs.AnyAsync(j => j.Id == jobId, ct))
            return null;

        return new JobTreeResponse(jobId, rows.Count, TreeBuilder.Build(rows));
    }

    public async Task<PageLinksResponse?> GetPageLinksAsync(Guid jobId, long pageId, CancellationToken ct)
    {
        var page = await _db.Pages
            .AsNoTracking()
            .Where(p => p.JobId == jobId && p.Id == pageId)
            .Select(p => new { p.Id, p.Url, p.OutgoingLinkCount, p.InternalLinkCount, p.DomainLinkRatio, p.OutgoingLinks, p.Job.StartingDomain })
            .FirstOrDefaultAsync(ct);
        if (page is null) return null;

        // Canonicalize once per request, not once per link.
        var canonical = DomainLinkRatio.Canonicalize(page.StartingDomain);
        var links = page.OutgoingLinks
            .Select(url => new PageLink(url, Uri.TryCreate(url, UriKind.Absolute, out var uri) && DomainLinkRatio.IsInternalTo(uri, canonical)))
            .ToList();

        return new PageLinksResponse(page.Id, page.Url, page.OutgoingLinkCount, page.InternalLinkCount, page.DomainLinkRatio, links);
    }

    public async Task<CancelJobResult> CancelAsync(Guid jobId, CancellationToken ct)
    {
        // Conditional update: atomic with respect to the worker finishing the job concurrently. The lease is left
        // alone on purpose: the worker's writes are filtered by its lease, so keeping it lets the worker notice the
        // cancel and settle the job with its final counters (and release the lease itself).
        var updated = await _db.Jobs
            .Where(j => j.Id == jobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CompletedAt, Now), ct);

        if (updated > 0)
        {
            _logger.LogInformation("Job {JobId} canceled", jobId);
            return new CancelJobResult(CancelJobOutcome.Canceled, new CancelJobResponse(jobId, JobStatus.Canceled));
        }

        var exists = await _db.Jobs.AnyAsync(j => j.Id == jobId, ct);
        return new CancelJobResult(exists ? CancelJobOutcome.AlreadyTerminal : CancelJobOutcome.NotFound, null);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private JobDetails ToDetails(CrawlJob job) => new(
        job.Id,
        job.InputUrl,
        job.StartingDomain,
        job.MaxDepth,
        job.MaxPages,
        job.Status,
        job.FailureReason,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.PagesCrawled,
        job.PagesDiscovered,
        JobProgress.Percent(job.Status, job.PagesCrawled, job.PagesDiscovered, job.MaxPages),
        JobProgress.DurationMs(job.StartedAt, job.CompletedAt, Now));
}

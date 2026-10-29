using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Hosting;

/// <summary>
/// Closes the two gaps a transactional outbox would close, with far less machinery:
///  1. A job saved as Pending whose publish failed (or whose API process died before publishing) is re-published
///     once a younger job has already started (the queue is FIFO, so being overtaken means the message is gone).
///     Re-publishing is safe because the worker treats the message as a trigger: a job already Running or
///     terminal is deferred/ignored, so a duplicate costs nothing.
///  2. A job stuck in Running whose worker lease expired long ago (worker crashed with its message already
///     acked, or the DLQ path could not reach the database) is marked Failed so the UI never shows it as live.
/// </summary>
public sealed class JobReconciler(
    IServiceScopeFactory scopeFactory,
    IJobPublisher publisher,
    TimeProvider time,
    ILogger<JobReconciler> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    /// <summary>How long a Pending job may wait before we assume its message was never delivered.</summary>
    public static readonly TimeSpan PendingGracePeriod = TimeSpan.FromSeconds(90);
    /// <summary>How long after lease expiry a Running job is declared lost (leases are ~2 min, renewed every few pages).</summary>
    public static readonly TimeSpan LostLeaseGracePeriod = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ReconcileOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            await RepublishStalePendingAsync(db, ct);
            await FailLostRunningAsync(db, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reconciliation pass failed; will retry on the next tick");
        }
    }

    /// <summary>Minimum spacing between re-publishes of the same job (a long queue is not a lost message).</summary>
    public static readonly TimeSpan RepublishBackoff = TimeSpan.FromMinutes(5);

    // Per-instance memory of what was re-published when; a restart simply starts the backoff again.
    private readonly Dictionary<Guid, DateTime> _lastRepublished = new();

    private async Task RepublishStalePendingAsync(CrawlerDbContext db, CancellationToken ct)
    {
        var now = Now;
        PruneRepublished(now);

        // "Overtaken" rule: the queue drains in order, so a Pending job is lost only once a job created after it has
        // already started — its message would have been delivered first. A Pending job nobody has overtaken is
        // simply still queued (e.g. behind a long crawl), however old it is, and re-publishing it would only add
        // duplicates. Unlike a global "any live lease" check, one busy worker cannot suppress recovery elsewhere.
        var cutoff = now - PendingGracePeriod;
        var stale = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending && j.CreatedAt < cutoff)
            .Where(j => db.Jobs.Any(y => y.StartedAt != null && y.StartedAt > j.CreatedAt))
            .OrderBy(j => j.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            if (_lastRepublished.TryGetValue(job.Id, out var last) && now - last < RepublishBackoff)
                continue;

            try
            {
                await publisher.PublishAsync(new CrawlJobRequested(job.Id, job.InputUrl, job.MaxDepth, job.MaxPages, now), $"reconciler:{job.Id:N}", ct);
                _lastRepublished[job.Id] = now;
                logger.LogWarning("Re-published job {JobId} that was Pending since {CreatedAt} and has been overtaken by a younger job",
                    job.Id, job.CreatedAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not re-publish Pending job {JobId}; broker still unavailable", job.Id);
                return;
            }
        }
    }

    /// <summary>Drops backoff entries that no longer suppress anything, so the map cannot grow without bound.</summary>
    private void PruneRepublished(DateTime now)
    {
        List<Guid>? expired = null;
        foreach (var (id, at) in _lastRepublished)
            if (now - at >= RepublishBackoff) (expired ??= []).Add(id);
        expired?.ForEach(id => _lastRepublished.Remove(id));
    }

    /// <summary>Number of jobs currently held in the re-publish backoff memory (exposed for tests).</summary>
    internal int RepublishBackoffCount => _lastRepublished.Count;

    private async Task FailLostRunningAsync(CrawlerDbContext db, CancellationToken ct)
    {
        var cutoff = Now - LostLeaseGracePeriod;
        var failed = await db.Jobs
            .Where(j => j.Status == JobStatus.Running && j.LeaseExpiresAt != null && j.LeaseExpiresAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.FailureReason, "Worker stopped reporting progress (lease expired)")
                .SetProperty(j => j.CompletedAt, Now)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null), ct);

        if (failed > 0)
            logger.LogWarning("Marked {Count} Running job(s) with expired leases as Failed", failed);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;
}

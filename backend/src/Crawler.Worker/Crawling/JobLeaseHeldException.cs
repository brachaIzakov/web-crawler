namespace Crawler.Worker.Crawling;

/// <summary>
/// Another worker holds a live lease on the job. Not a failure: the message is deferred until the lease
/// would expire (<see cref="RetryAfter"/>), at which point the job is either finished or claimable.
/// </summary>
public sealed class JobLeaseHeldException(Guid jobId, TimeSpan retryAfter)
    : Exception($"Crawl job {jobId} is leased by another worker; retry after {retryAfter.TotalSeconds:0}s.")
{
    public Guid JobId { get; } = jobId;
    public TimeSpan RetryAfter { get; } = retryAfter;
}

namespace Crawler.Worker.Crawling;

/// <summary>The message references a job that does not exist. Non-transient: retrying cannot help.</summary>
public sealed class JobNotFoundException(Guid jobId) : Exception($"Crawl job {jobId} was not found.")
{
    public Guid JobId { get; } = jobId;
}

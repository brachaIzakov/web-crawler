namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Published by the API when a job is created; consumed by the worker.
/// Serialized as JSON (camelCase). The job row is the source of truth — the message only carries
/// what the worker needs to start, and is safe to deliver more than once.
/// </summary>
public sealed record CrawlJobRequested(
    Guid JobId,
    string Url,
    int MaxDepth,
    int MaxPages,
    DateTime RequestedAt)
{
    public const string RoutingKey = "crawl.job.requested";
}

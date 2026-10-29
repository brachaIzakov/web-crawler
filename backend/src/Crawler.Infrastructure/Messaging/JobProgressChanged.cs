using Crawler.Domain.Entities;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Published by the worker (fanout exchange <see cref="Exchange"/>) whenever a job's progress counters are
/// flushed or its status changes. Consumed by the API to push live updates to UI clients over SSE.
/// Purely a notification: the database remains the source of truth and consumers re-read it.
/// </summary>
public sealed record JobProgressChanged(
    Guid JobId,
    JobStatus Status,
    int PagesCrawled,
    int PagesDiscovered,
    DateTime OccurredAt)
{
    public const string Exchange = "crawl.events";
    public const string RoutingKey = "crawl.job.progress";
}

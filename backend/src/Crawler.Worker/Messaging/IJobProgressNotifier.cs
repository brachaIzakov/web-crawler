using Crawler.Infrastructure.Messaging;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Broadcasts <see cref="JobProgressChanged"/> notifications for live UI updates.
/// Implementations are best-effort and never throw: the database stays the source of truth.
/// </summary>
public interface IJobProgressNotifier
{
    Task NotifyAsync(JobProgressChanged evt, CancellationToken ct);
}

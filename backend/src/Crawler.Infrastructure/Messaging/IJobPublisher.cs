namespace Crawler.Infrastructure.Messaging;

public interface IJobPublisher
{
    Task PublishAsync(CrawlJobRequested message, string correlationId, CancellationToken cancellationToken = default);
}

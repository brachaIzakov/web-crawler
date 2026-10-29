using Crawler.Worker.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Worker.Health;

/// <summary>Ready only once the consumer holds an open connection and channel.</summary>
public sealed class RabbitMqConsumerHealthCheck(CrawlJobConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(consumer.IsConnected
            ? HealthCheckResult.Healthy("Consumer connected")
            : HealthCheckResult.Unhealthy("Consumer is not connected to RabbitMQ"));
}

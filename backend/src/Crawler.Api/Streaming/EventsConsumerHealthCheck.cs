using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Api.Streaming;

public sealed class EventsConsumerHealthCheck(JobEventsConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(consumer.IsConnected
            ? HealthCheckResult.Healthy("Subscribed to job events")
            : HealthCheckResult.Unhealthy("Not connected to the job events exchange"));
}

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Crawler.Api.Http;

public static class RateLimitPolicies
{
    public const string CreateJob = "create-job";

    /// <summary>Per-client cap on job creation: each job can cost up to 1000 fetches, so creation is the lever to protect.</summary>
    public static IServiceCollection AddCrawlerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitPerMinute = configuration.GetValue("RateLimits:CreateJobPerMinute", 30);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(CreateJob, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
    }
}

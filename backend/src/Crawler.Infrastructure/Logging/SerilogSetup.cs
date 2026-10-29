using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Crawler.Infrastructure.Logging;

/// <summary>
/// Structured JSON logging to stdout. JobId / CorrelationId are pushed via LogContext by the callers.
/// </summary>
public static class SerilogSetup
{
    public static IHostBuilder UseCrawlerSerilog(this IHostBuilder host, string serviceName)
    {
        return host.UseSerilog((context, services, config) =>
        {
            config
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning);

            if (context.HostingEnvironment.IsDevelopment())
                config.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
            else
                config.WriteTo.Console(new CompactJsonFormatter());
        });
    }
}

using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Api.Http;

public static class HealthResponseWriter
{
    public static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), error = ErrorFor(entry.Value) })
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, body, cancellationToken: context.RequestAborted);
    }

    /// <summary>
    /// The check's own description, or a fixed string. Never the exception message: it can leak hosts, ports,
    /// credentials or stack details to unauthenticated callers (the exception is still logged by the health service).
    /// </summary>
    private static string? ErrorFor(HealthReportEntry entry)
    {
        if (entry.Status == HealthStatus.Healthy) return null;
        // A check that throws gets ex.Message as its Description from the health service; treat that as no description.
        var description = entry.Description;
        if (string.IsNullOrWhiteSpace(description) || description == entry.Exception?.Message)
            return "check failed";
        return description;
    }
}

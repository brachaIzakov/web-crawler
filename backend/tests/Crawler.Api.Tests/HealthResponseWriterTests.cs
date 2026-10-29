using System.Text.Json.Nodes;
using Crawler.Api.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Api.Tests;

public class HealthResponseWriterTests
{
    private static async Task<JsonNode> WriteAsync(params (string Name, HealthReportEntry Entry)[] entries)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var report = new HealthReport(entries.ToDictionary(e => e.Name, e => e.Entry), TimeSpan.Zero);

        await HealthResponseWriter.WriteJson(context, report);

        return JsonNode.Parse(body.ToArray())!;
    }

    [Fact]
    public async Task WriteJson_FailedCheckWithException_NeverExposesExceptionMessage()
    {
        var json = await WriteAsync(("db", new HealthReportEntry(HealthStatus.Unhealthy, null, TimeSpan.Zero,
            new InvalidOperationException("Host=secret-db;Password=hunter2"), null)));

        json["checks"]!["db"]!["error"]!.GetValue<string>().Should().Be("check failed");
        json.ToJsonString().Should().NotContain("hunter2");
    }

    [Fact]
    public async Task WriteJson_FailedCheckWithDescription_UsesDescription()
    {
        var json = await WriteAsync(("events", new HealthReportEntry(HealthStatus.Unhealthy, "Events consumer is not connected",
            TimeSpan.Zero, new Exception("raw detail"), null)));

        json["checks"]!["events"]!["error"]!.GetValue<string>().Should().Be("Events consumer is not connected");
    }

    [Fact]
    public async Task WriteJson_ThrowingCheck_DescriptionCopiedFromException_IsNotExposed()
    {
        var ex = new InvalidOperationException("connect to 10.0.0.5:5672 failed");
        var json = await WriteAsync(("rabbit", new HealthReportEntry(HealthStatus.Unhealthy, ex.Message, TimeSpan.Zero, ex, null)));

        json["checks"]!["rabbit"]!["error"]!.GetValue<string>().Should().Be("check failed");
    }

    [Fact]
    public async Task WriteJson_HealthyCheck_HasNoError()
    {
        var json = await WriteAsync(("db", new HealthReportEntry(HealthStatus.Healthy, "ok", TimeSpan.Zero, null, null)));

        json["status"]!.GetValue<string>().Should().Be("Healthy");
        json["checks"]!["db"]!["error"].Should().BeNull();
    }
}

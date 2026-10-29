using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Crawler.Api.Contracts;
using Crawler.Api.Services;
using Crawler.Api.Streaming;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Crawler.Api.Tests;

public sealed class JobEventsEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public JobEventsEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record SseEvent(string Name, string Data);

    /// <summary>Reads one SSE event (skipping keep-alive comments).</summary>
    private static async Task<SseEvent> ReadEventAsync(StreamReader reader, CancellationToken ct)
    {
        string? name = null, data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new EndOfStreamException();
            if (line.Length == 0)
            {
                if (name is not null) return new SseEvent(name, data ?? "");
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event: ")) name = line["event: ".Length..];
            else if (line.StartsWith("data: ")) data = line["data: ".Length..];
        }
    }

    private async Task<CrawlJob> SeedAsync(JobStatus status)
    {
        var job = Seed.Job(status, crawled: 1, discovered: 4);
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); return db.SaveChangesAsync(); });
        return job;
    }

    [Fact]
    public async Task Events_UnknownJob_Returns404()
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/events");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Events_TerminalJob_SendsSnapshotThenEndAndCloses()
    {
        var job = await SeedAsync(JobStatus.Completed);
        using var cts = new CancellationTokenSource(Timeout);

        using var response = await _client.GetAsync($"/api/jobs/{job.Id}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var first = await ReadEventAsync(reader, cts.Token);
        first.Name.Should().Be("job");
        var data = JsonNode.Parse(first.Data)!;
        data["jobId"]!.GetValue<string>().Should().Be(job.Id.ToString());
        data["status"]!.GetValue<string>().Should().Be("Completed");
        data["progressPercent"]!.GetValue<int>().Should().Be(100);

        (await ReadEventAsync(reader, cts.Token)).Name.Should().Be("end");
        (await reader.ReadToEndAsync(cts.Token)).Trim().Should().BeEmpty("the stream closes after the end event");
    }

    [Fact]
    public async Task Events_RunningJob_PushesUpdateWhenHubIsNotified()
    {
        var job = await SeedAsync(JobStatus.Running);
        using var cts = new CancellationTokenSource(Timeout);

        using var response = await _client.GetAsync($"/api/jobs/{job.Id}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        var first = await ReadEventAsync(reader, cts.Token);
        JsonNode.Parse(first.Data)!["status"]!.GetValue<string>().Should().Be("Running");

        await _factory.WithDbAsync(db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Completed)
            .SetProperty(j => j.PagesCrawled, 4)
            .SetProperty(j => j.CompletedAt, DateTime.UtcNow)));
        _factory.Services.GetRequiredService<JobProgressHub>()
            .Publish(new JobProgressChanged(job.Id, JobStatus.Completed, 4, 4, DateTime.UtcNow));

        var update = await ReadEventAsync(reader, cts.Token);
        update.Name.Should().Be("job");
        var data = JsonNode.Parse(update.Data)!;
        data["status"]!.GetValue<string>().Should().Be("Completed", "the stream re-reads the database on every hint");
        data["pagesCrawled"]!.GetValue<int>().Should().Be(4);

        (await ReadEventAsync(reader, cts.Token)).Name.Should().Be("end");
    }

    [Fact]
    public async Task Events_HintWithoutDatabaseChange_IsNotResent()
    {
        var job = await SeedAsync(JobStatus.Running);
        using var cts = new CancellationTokenSource(Timeout);
        using var response = await _client.GetAsync($"/api/jobs/{job.Id}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        await ReadEventAsync(reader, cts.Token);

        _factory.Services.GetRequiredService<JobProgressHub>()
            .Publish(new JobProgressChanged(job.Id, JobStatus.Running, 1, 4, DateTime.UtcNow));

        // The stream re-reads the job, finds the same state (only DurationMs moved) and writes nothing.
        using var shortCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        shortCts.CancelAfter(TimeSpan.FromSeconds(1));
        var act = () => ReadEventAsync(reader, shortCts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Events_ServerStopping_ClosesOpenStreamWithoutEndEvent()
    {
        var jobs = Substitute.For<IJobService>();
        var details = new JobDetails(Guid.NewGuid(), "https://example.com/", "example.com", 2, 200, JobStatus.Running,
            null, DateTime.UtcNow, DateTime.UtcNow, null, 1, 4, 25, 0);
        jobs.GetAsync(details.JobId, Arg.Any<CancellationToken>()).Returns(details);
        var lifetime = new StoppableLifetime();
        var stream = new JobEventStream(new JobProgressHub(), Options.Create(new JsonOptions()), lifetime);
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;

        var run = stream.RunAsync(http, details.JobId, jobs);
        await Task.Delay(200);
        run.IsCompleted.Should().BeFalse("a running job keeps its stream open");

        lifetime.Stop();

        (await run.WaitAsync(Timeout)).Should().BeTrue();
        var written = Encoding.UTF8.GetString(body.ToArray());
        written.Should().StartWith("event: job\n");
        written.Should().NotContain("event: end", "EventSource must reconnect to another instance, not stop");
    }

    private sealed class StoppableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Stop() => StopApplication();
    }

    [Fact]
    public async Task Events_NotificationForOtherJob_DoesNotWakeStream()
    {
        var job = await SeedAsync(JobStatus.Running);
        using var cts = new CancellationTokenSource(Timeout);
        using var response = await _client.GetAsync($"/api/jobs/{job.Id}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        await ReadEventAsync(reader, cts.Token);

        _factory.Services.GetRequiredService<JobProgressHub>()
            .Publish(new JobProgressChanged(Guid.NewGuid(), JobStatus.Completed, 1, 1, DateTime.UtcNow));

        // No event for this job within 1 s (the safety refresh is 5 s).
        using var shortCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        shortCts.CancelAfter(TimeSpan.FromSeconds(1));
        var act = () => ReadEventAsync(reader, shortCts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

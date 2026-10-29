using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Crawler.Api.Contracts;
using Crawler.Api.Services;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Crawler.Api.Streaming;

/// <summary>
/// Streams a job's state as Server-Sent Events: a <c>job</c> event with the full <see cref="JobDetails"/>
/// on connect and on every change, comment keep-alives while idle, and an <c>end</c> event once terminal.
/// On server shutdown the stream is closed without <c>end</c>, so EventSource reconnects (to another instance).
/// </summary>
public sealed class JobEventStream
{
    private static readonly TimeSpan SafetyRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly byte[] KeepAliveFrame = Encoding.UTF8.GetBytes(": keep-alive\n\n");
    private static readonly byte[] EndFrame = Encoding.UTF8.GetBytes("event: end\ndata: {}\n\n");

    private readonly JobProgressHub _hub;
    private readonly JsonSerializerOptions _json;
    private readonly CancellationToken _shutdown;

    public JobEventStream(
        JobProgressHub hub, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions, IHostApplicationLifetime lifetime)
    {
        _hub = hub;
        _json = jsonOptions.Value.SerializerOptions;
        _shutdown = lifetime.ApplicationStopping;
    }

    /// <summary>Streams until the job is terminal or the client disconnects. Returns false (nothing written) if the job does not exist.</summary>
    public async Task<bool> RunAsync(HttpContext http, Guid jobId, IJobService jobs)
    {
        // Kestrel's graceful shutdown waits for open requests; without this, every open stream would hold it up.
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _shutdown);
        var ct = streamCts.Token;
        var events = JobProgressHub.CreateChannel();
        // Subscribe before the first read so no change between the read and the subscription is missed.
        using var subscription = _hub.Subscribe(jobId, events.Writer);

        try
        {
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null)
                return false;

            PrepareResponse(http);
            await StreamAsync(http.Response, job, jobId, jobs, events.Reader, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected or the server is stopping.
        }

        return true;
    }

    private static void PrepareResponse(HttpContext http)
    {
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var headers = http.Response.Headers;
        headers.ContentType = "text/event-stream; charset=utf-8";
        headers.CacheControl = "no-cache";
        headers.Connection = "keep-alive";
        headers["X-Accel-Buffering"] = "no";
    }

    private async Task StreamAsync(
        HttpResponse response, JobDetails job, Guid jobId, IJobService jobs,
        ChannelReader<JobProgressChanged> events, CancellationToken ct)
    {
        await WriteJobAsync(response, job, ct);
        var sinceRefresh = Stopwatch.StartNew();
        var sinceWrite = Stopwatch.StartNew();

        while (!IsTerminal(job.Status))
        {
            var refreshDue = SafetyRefreshInterval - sinceRefresh.Elapsed;
            var keepAliveDue = KeepAliveInterval - sinceWrite.Elapsed;
            var gotEvent = await WaitForEventAsync(events, Min(refreshDue, keepAliveDue), ct);

            if (gotEvent || sinceRefresh.Elapsed >= SafetyRefreshInterval)
            {
                DrainPending(events);
                // Events are only change hints: the DB is the source of truth, and re-reading also coalesces bursts.
                var latest = await jobs.GetAsync(jobId, ct) ?? job;
                sinceRefresh.Restart();
                // The periodic re-read only catches lost events; an unchanged job is not re-sent (keep-alives continue).
                if (!HasChanged(job, latest)) continue;

                job = latest;
                await WriteJobAsync(response, job, ct);
                sinceWrite.Restart();
            }
            else if (sinceWrite.Elapsed >= KeepAliveInterval)
            {
                await WriteFrameAsync(response, KeepAliveFrame, ct);
                sinceWrite.Restart();
            }
        }

        await WriteFrameAsync(response, EndFrame, ct);
    }

    private static async Task<bool> WaitForEventAsync(ChannelReader<JobProgressChanged> events, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
            return events.TryPeek(out _);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await events.WaitToReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void DrainPending(ChannelReader<JobProgressChanged> events)
    {
        while (events.TryRead(out _)) { }
    }

    private Task WriteJobAsync(HttpResponse response, JobDetails job, CancellationToken ct)
    {
        var frame = $"event: job\ndata: {JsonSerializer.Serialize(job, _json)}\n\n";
        return WriteFrameAsync(response, Encoding.UTF8.GetBytes(frame), ct);
    }

    private static async Task WriteFrameAsync(HttpResponse response, byte[] frame, CancellationToken ct)
    {
        await response.Body.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    // DurationMs of a running job grows on every read, so it alone is not a change.
    private static bool HasChanged(JobDetails previous, JobDetails latest)
        => previous with { DurationMs = null } != latest with { DurationMs = null };

    private static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

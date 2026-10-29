using System.Threading.Channels;
using Crawler.Infrastructure.Messaging;

namespace Crawler.Api.Streaming;

/// <summary>In-process pub/sub that fans <see cref="JobProgressChanged"/> events out to the SSE streams watching a job.</summary>
public sealed class JobProgressHub
{
    private readonly Dictionary<Guid, List<ChannelWriter<JobProgressChanged>>> _subscribers = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a reader for a job's events. Bounded with DropOldest so a slow client can never block the
    /// broker consumer; events are only "something changed" hints, so losing old ones is harmless.
    /// </summary>
    public static Channel<JobProgressChanged> CreateChannel() =>
        Channel.CreateBounded<JobProgressChanged>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public IDisposable Subscribe(Guid jobId, ChannelWriter<JobProgressChanged> writer)
    {
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(jobId, out var writers))
                _subscribers[jobId] = writers = new List<ChannelWriter<JobProgressChanged>>();
            writers.Add(writer);
        }

        return new Subscription(this, jobId, writer);
    }

    public void Publish(JobProgressChanged evt)
    {
        ChannelWriter<JobProgressChanged>[] targets;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(evt.JobId, out var writers))
                return;
            targets = writers.ToArray();
        }

        foreach (var writer in targets)
            writer.TryWrite(evt);
    }

    private void Unsubscribe(Guid jobId, ChannelWriter<JobProgressChanged> writer)
    {
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(jobId, out var writers))
                return;
            writers.Remove(writer);
            if (writers.Count == 0)
                _subscribers.Remove(jobId);
        }

        writer.TryComplete();
    }

    private sealed class Subscription(JobProgressHub hub, Guid jobId, ChannelWriter<JobProgressChanged> writer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                hub.Unsubscribe(jobId, writer);
        }
    }
}

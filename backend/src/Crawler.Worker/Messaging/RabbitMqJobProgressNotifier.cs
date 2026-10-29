using System.Threading.Channels;
using Crawler.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Publishes progress events (non-persistent, no confirms) to the events fanout exchange without ever blocking the
/// caller: <see cref="NotifyAsync"/> only enqueues into a bounded queue (DropOldest — events are "something changed"
/// hints and the newest one, e.g. the terminal status, is the one that matters). A single background loop drains the
/// queue and is the only user of the connection/channel, so IModel (not thread-safe) needs no lock and a slow broker
/// connect (up to the connection timeout) delays only the UI, never the crawl's progress flush or lease renewal.
/// After a failure, events are dropped for <see cref="ReconnectCooldown"/> so a broker outage does not trigger a
/// connection attempt per event. Never throws: the database stays the source of truth.
/// </summary>
public sealed class RabbitMqJobProgressNotifier : IJobProgressNotifier, IDisposable
{
    internal const int QueueCapacity = 64;
    internal static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IProgressEventTransport _transport;
    private readonly TimeProvider _time;
    private readonly ILogger<RabbitMqJobProgressNotifier> _logger;
    private readonly Channel<JobProgressChanged> _queue = Channel.CreateBounded<JobProgressChanged>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Task _drain;
    // Only touched by the drain loop.
    private DateTimeOffset _nextConnectAttempt = DateTimeOffset.MinValue;

    public RabbitMqJobProgressNotifier(
        IOptions<RabbitMqOptions> options, TimeProvider timeProvider, ILogger<RabbitMqJobProgressNotifier> logger)
        : this(new RabbitMqProgressEventTransport(options.Value, timeProvider), timeProvider, logger)
    {
    }

    internal RabbitMqJobProgressNotifier(
        IProgressEventTransport transport, TimeProvider timeProvider, ILogger<RabbitMqJobProgressNotifier> logger)
    {
        _transport = transport;
        _time = timeProvider;
        _logger = logger;
        _drain = Task.Run(DrainAsync);
    }

    public Task NotifyAsync(JobProgressChanged evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        // Never fails while open (DropOldest); after Dispose the event is simply dropped.
        _queue.Writer.TryWrite(evt);
        return Task.CompletedTask;
    }

    private async Task DrainAsync()
    {
        await foreach (var evt in _queue.Reader.ReadAllAsync())
            PublishOne(evt);
    }

    private void PublishOne(JobProgressChanged evt)
    {
        if (_time.GetUtcNow() < _nextConnectAttempt) return;

        try
        {
            _transport.Publish(evt);
        }
        catch (Exception ex)
        {
            _transport.Reset();
            _nextConnectAttempt = _time.GetUtcNow() + ReconnectCooldown;
            _logger.LogWarning("Could not publish progress event for job {JobId} ({Status}): {Error}",
                evt.JobId, evt.Status, ex.Message);
        }
    }

    /// <summary>Flushes queued events (best effort, bounded wait) so a terminal status sent just before shutdown is not lost.</summary>
    public void Dispose()
    {
        if (!_queue.Writer.TryComplete()) return;

        // Disposing the transport while the loop is still inside a publish would race on the channel; if the loop
        // is stuck (e.g. connecting to an unreachable broker) the process is exiting anyway.
        if (DrainCompleted())
            _transport.Dispose();
    }

    private bool DrainCompleted()
    {
        try
        {
            return _drain.Wait(DisposeDrainTimeout);
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "Progress event loop ended with an error");
            return true;
        }
    }
}

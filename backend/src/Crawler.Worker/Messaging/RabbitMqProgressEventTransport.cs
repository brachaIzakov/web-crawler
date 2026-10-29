using System.Text.Json;
using Crawler.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace Crawler.Worker.Messaging;

/// <summary>Synchronous publish seam used by <see cref="RabbitMqJobProgressNotifier"/>'s single drain loop (never called concurrently).</summary>
internal interface IProgressEventTransport : IDisposable
{
    /// <summary>Publishes one event, connecting first if needed. Throws on failure.</summary>
    void Publish(JobProgressChanged evt);

    /// <summary>Drops the connection (best effort, never throws) so the next publish reconnects.</summary>
    void Reset();
}

/// <summary>Owns the events connection/channel; created lazily and re-created after <see cref="Reset"/>.</summary>
internal sealed class RabbitMqProgressEventTransport(RabbitMqOptions options, TimeProvider timeProvider) : IProgressEventTransport
{
    private const string ClientName = "crawler-worker-events";

    private IConnection? _connection;
    private IModel? _channel;

    public void Publish(JobProgressChanged evt)
    {
        var channel = GetOrCreateChannel();
        var props = channel.CreateBasicProperties();
        props.Persistent = false;
        props.ContentType = "application/json";
        props.Type = nameof(JobProgressChanged);
        props.Timestamp = new AmqpTimestamp(timeProvider.GetUtcNow().ToUnixTimeSeconds());

        var body = JsonSerializer.SerializeToUtf8Bytes(evt, MessageSerialization.Options);
        channel.BasicPublish(options.EventsExchange, JobProgressChanged.RoutingKey, mandatory: false, props, body);
    }

    private IModel GetOrCreateChannel()
    {
        if (_channel is { IsOpen: true }) return _channel;

        Reset();
        _connection = RabbitMqTopology.CreateConnection(options, ClientName);
        _channel = _connection.CreateModel();
        RabbitMqTopology.Declare(_channel, options);
        return _channel;
    }

    public void Reset()
    {
        try { _channel?.Dispose(); } catch { /* best effort */ }
        try { _connection?.Dispose(); } catch { /* best effort */ }
        _channel = null;
        _connection = null;
    }

    public void Dispose() => Reset();
}

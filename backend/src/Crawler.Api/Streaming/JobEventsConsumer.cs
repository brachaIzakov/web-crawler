using System.Text.Json;
using Crawler.Infrastructure.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Api.Streaming;

/// <summary>
/// Listens on the events fanout exchange through an exclusive, auto-delete, server-named queue, so every API
/// instance receives every event, and forwards them to the <see cref="JobProgressHub"/>.
/// A channel that closes while its connection stays open is not reopened by the client's automatic recovery, so a
/// watchdog rebuilds the channel, queue and consumer on the live connection (mirrors the worker's CrawlJobConsumer).
/// </summary>
public sealed class JobEventsConsumer : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ChannelCheckInterval = TimeSpan.FromSeconds(5);

    private readonly RabbitMqOptions _options;
    private readonly JobProgressHub _hub;
    private readonly ILogger<JobEventsConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public JobEventsConsumer(IOptions<RabbitMqOptions> options, JobProgressHub hub, ILogger<JobEventsConsumer> logger)
    {
        _options = options.Value;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>True while the broker connection is open (automatic recovery reopens it after an outage).</summary>
    public bool IsConnected => _connection?.IsOpen == true && _channel?.IsOpen == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Connecting is synchronous; yield so a broker outage never delays host startup.
        await Task.Yield();

        if (!await ConnectWithRetryAsync(stoppingToken)) return;

        // Watchdog: automatic recovery handles a dropped connection, but not a channel closed on a live connection.
        while (await DelayAsync(ChannelCheckInterval, stoppingToken))
        {
            if (!NeedsRebuild(_connection?.IsOpen == true, _channel?.IsOpen == true)) continue;

            _logger.LogWarning("Events channel closed while its connection is open; rebuilding channel, queue and consumer");
            try
            {
                OpenChannel();
                _logger.LogInformation("Events consumer rebuilt on exchange {Exchange}", _options.EventsExchange);
            }
            catch (Exception ex)
            {
                // Next tick retries; if the connection itself is gone, recovery or the next rebuild handles it.
                _logger.LogWarning("Could not rebuild the events channel, retrying in {Delay}s: {Error}",
                    ChannelCheckInterval.TotalSeconds, ex.Message);
            }
        }
    }

    /// <summary>
    /// True when the consumer must be rebuilt: the connection is open (otherwise automatic recovery owns it)
    /// but the channel is closed or missing.
    /// </summary>
    internal static bool NeedsRebuild(bool connectionOpen, bool channelOpen) => connectionOpen && !channelOpen;

    /// <summary>Retries the full connect until it succeeds; false on shutdown.</summary>
    private async Task<bool> ConnectWithRetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Connect();
                _logger.LogInformation("Listening for job events on exchange {Exchange}", _options.EventsExchange);
                return true;
            }
            catch (Exception ex)
            {
                CloseConnection();
                _logger.LogWarning("Events consumer could not connect to RabbitMQ, retrying in {Delay}s: {Error}",
                    RetryDelay.TotalSeconds, ex.Message);
            }

            if (!await DelayAsync(RetryDelay, stoppingToken)) return false;
        }
        return false;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Connect()
    {
        _connection = RabbitMqTopology.CreateConnection(_options, "crawler-api-events");
        OpenChannel();
    }

    /// <summary>Opens a fresh channel on the current connection and declares the exclusive queue and consumer.</summary>
    private void OpenChannel()
    {
        var connection = _connection ?? throw new InvalidOperationException("Not connected");
        try { _channel?.Dispose(); } catch { /* best effort: the old channel is already closed */ }
        _channel = null;

        var channel = connection.CreateModel();
        try
        {
            channel.ModelShutdown += (_, args) =>
                _logger.LogWarning("Events channel shut down ({Initiator}): {ReplyCode} {ReplyText}", args.Initiator, args.ReplyCode, args.ReplyText);
            RabbitMqTopology.Declare(channel, _options);

            var queue = channel.QueueDeclare(queue: string.Empty, durable: false, exclusive: true, autoDelete: true).QueueName;
            channel.QueueBind(queue, _options.EventsExchange, routingKey: string.Empty);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += OnReceived;
            channel.BasicConsume(queue, autoAck: true, consumer);
        }
        catch
        {
            try { channel.Dispose(); } catch { /* best effort */ }
            throw;
        }
        _channel = channel;
    }

    private Task OnReceived(object sender, BasicDeliverEventArgs args)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<JobProgressChanged>(args.Body.Span, MessageSerialization.Options);
            if (evt is null || evt.JobId == Guid.Empty)
                _logger.LogWarning("Ignoring empty job progress event");
            else
                _hub.Publish(evt);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring malformed job progress event");
        }

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        CloseConnection();
    }

    public override void Dispose()
    {
        CloseConnection();
        base.Dispose();
    }

    private void CloseConnection()
    {
        try
        {
            _channel?.Dispose();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while closing the events connection");
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }
}

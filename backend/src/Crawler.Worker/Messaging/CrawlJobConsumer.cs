using System.Globalization;
using System.Text;
using System.Text.Json;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Serilog.Context;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Consumes <see cref="CrawlJobRequested"/> messages with manual acknowledgements.
///
/// Failure policy:
///   * success                         → ack
///   * worker shutting down            → nack(requeue) — the job resumes on the next delivery
///   * poison message (bad JSON)       → nack(no requeue) → DLQ
///   * job leased by another worker    → publish a copy to the retry queue with a TTL of the remaining lease
///                                       (x-retry-count unchanged — this is not a failure), then ack the original
///   * transient, retries left         → publish a copy to the retry queue with x-retry-count = n+1 and a
///                                       per-message TTL of base·2^n, then ack the original
///   * retry publish fails             → nack(requeue) — nothing is lost, the delivery is retried immediately
///   * transient, retries exhausted    → nack(no requeue) → DLQ, job marked Failed
///   * non-transient                   → nack(no requeue) → DLQ, job marked Failed
///   * settling itself throws          → one guarded nack(requeue); if the channel is gone the broker redelivers
///
/// Every delivery is settled on the channel that delivered it (delivery tags are per channel). A channel that
/// closes while its connection stays open (e.g. WaitForConfirmsOrDie timing out, a channel-level protocol error)
/// is not reopened by the client's automatic recovery, so a watchdog rebuilds the consumer in that case.
///
/// Delayed retries never block the consumer: the retry queue has no consumer and dead-letters each message
/// back to the main exchange when its TTL expires. Note that RabbitMQ only expires messages at the head of a
/// queue, so a long delay ahead of a shorter one holds the shorter one back (acceptable: delays only grow).
/// </summary>
public sealed class CrawlJobConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    TimeProvider timeProvider,
    IJobProgressNotifier progressNotifier,
    WorkerIdentity workerIdentity,
    ILogger<CrawlJobConsumer> logger) : BackgroundService
{
    private const string ClientName = "crawler-worker";
    private const string BrokerDeathHeader = "x-death";
    private const int MaxFailureReasonLength = 1024;
    private static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublishConfirmTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ChannelCheckInterval = TimeSpan.FromSeconds(5);

    private readonly RabbitMqOptions _options = options.Value;
    // Held while a delivery is processed so shutdown can wait for the ack/nack before closing the channel.
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private IConnection? _connection;
    private IModel? _channel;
    private CancellationToken _stoppingToken;

    public bool IsConnected => _connection?.IsOpen == true && _channel?.IsOpen == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        while (true)
        {
            var consumerTag = await StartConsumingWithRetryAsync(stoppingToken);
            if (consumerTag is null) return;

            if (!await WaitForUnrecoverableChannelAsync(stoppingToken))
            {
                await StopConsumingAsync(consumerTag);
                return;
            }

            logger.LogWarning("RabbitMQ channel closed while its connection is open; rebuilding the consumer");
            CloseQuietly();
        }
    }

    /// <summary>True when the consumer must be rebuilt; false on shutdown.</summary>
    private async Task<bool> WaitForUnrecoverableChannelAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ChannelCheckInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (NeedsRebuild(_connection, _channel)) return true;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        return false;
    }

    /// <summary>
    /// Automatic recovery only reopens channels after a connection failure. A closed channel on an open connection
    /// stays closed, and with it the consumer; a closed connection is left to automatic recovery.
    /// </summary>
    public static bool NeedsRebuild(IConnection? connection, IModel? channel)
        => connection is { IsOpen: true } && channel is { IsOpen: false };

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _inFlight.Dispose();
        base.Dispose();
    }

    // ---------------------------------------------------------------- connection lifecycle

    /// <summary>
    /// The broker may start after the worker (docker compose), so keep trying until shutdown. The whole setup
    /// (connection, channel, topology, QoS, consume) is retried as one unit. Returns the consumer tag, or null on shutdown.
    /// </summary>
    private async Task<string?> StartConsumingWithRetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                return StartConsuming();
            }
            catch (Exception ex)
            {
                CloseQuietly();
                logger.LogWarning("RabbitMQ consumer setup failed at {Host}:{Port} ({Error}); retrying in {Delay}s",
                    _options.Host, _options.Port, ex.Message, ConnectRetryInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(ConnectRetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return null;
    }

    private string StartConsuming()
    {
        _connection = RabbitMqTopology.CreateConnection(_options, ClientName);
        logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", _options.Host, _options.Port);

        _channel = _connection.CreateModel();
        _channel.ModelShutdown += (_, args) =>
            logger.LogWarning("RabbitMQ channel shut down ({Initiator}): {ReplyCode} {ReplyText}", args.Initiator, args.ReplyCode, args.ReplyText);
        RabbitMqTopology.Declare(_channel, _options);
        _channel.BasicQos(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false);
        _channel.ConfirmSelect();

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnReceivedAsync;
        var consumerTag = _channel.BasicConsume(_options.Queue, autoAck: false, consumer);
        logger.LogInformation("Consuming from queue {Queue}", _options.Queue);
        return consumerTag;
    }

    private void CloseQuietly()
    {
        try { _channel?.Dispose(); } catch { /* best effort */ }
        try { _connection?.Dispose(); } catch { /* best effort */ }
        _channel = null;
        _connection = null;
    }

    private async Task StopConsumingAsync(string consumerTag)
    {
        try
        {
            if (_channel?.IsOpen == true) _channel.BasicCancel(consumerTag);
            if (!await _inFlight.WaitAsync(ShutdownDrainTimeout))
                logger.LogWarning("In-flight delivery did not settle within {Timeout}s; closing anyway (the broker redelivers it)",
                    ShutdownDrainTimeout.TotalSeconds);
            _channel?.Close();
            _connection?.Close();
            logger.LogInformation("RabbitMQ consumer stopped");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while stopping the RabbitMQ consumer");
        }
    }

    // ---------------------------------------------------------------- message handling

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        // Not the current _channel: after a rebuild that is a different channel, where this delivery tag is unknown.
        var channel = ((IBasicConsumer)sender).Model;
        await _inFlight.WaitAsync();
        try
        {
            using (LogContext.PushProperty("CorrelationId", ReadCorrelationId(delivery.BasicProperties)))
            {
                await HandleDeliveryAsync(channel, delivery);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to settle delivery {DeliveryTag}; trying to requeue it", delivery.DeliveryTag);
            TryRequeue(channel, delivery);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>Last resort after a failed ack/nack/republish, so a delivery is never left unsettled silently.</summary>
    private void TryRequeue(IModel channel, BasicDeliverEventArgs delivery)
    {
        try
        {
            if (channel.IsOpen)
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            else
                logger.LogWarning("Channel is closed; the broker redelivers delivery {DeliveryTag}", delivery.DeliveryTag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not requeue delivery {DeliveryTag}; the broker redelivers it when the channel closes", delivery.DeliveryTag);
        }
    }

    private async Task HandleDeliveryAsync(IModel channel, BasicDeliverEventArgs delivery)
    {
        if (!TryDeserialize(delivery.Body, out var message))
        {
            logger.LogError("Poison message {MessageId}: body is not a valid {MessageType}; sending to DLQ",
                delivery.BasicProperties.MessageId, nameof(CrawlJobRequested));
            channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        using (LogContext.PushProperty("JobId", message.JobId))
        {
            try
            {
                await RunCrawlAsync(message);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Shutdown requested; requeueing job so it resumes on another delivery");
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            }
            catch (JobLeaseHeldException ex)
            {
                logger.LogInformation("Job is leased by another worker; deferring the message for {Delay}s", Math.Ceiling(ex.RetryAfter.TotalSeconds));
                PublishDelayedOrRequeue(channel, delivery, ReadRetryCount(delivery.BasicProperties), ex.RetryAfter);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(channel, delivery, message, ex);
            }
        }
    }

    private async Task RunCrawlAsync(CrawlJobRequested message)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<CrawlEngine>();
        await engine.RunAsync(message, _stoppingToken);
    }

    private async Task HandleFailureAsync(IModel channel, BasicDeliverEventArgs delivery, CrawlJobRequested message, Exception exception)
    {
        var decision = DeliveryFailurePolicy.Decide(
            exception, ReadRetryCount(delivery.BasicProperties), _options.MaxRetries,
            TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds));

        switch (decision.Action)
        {
            case DeliveryFailureAction.DeadLetterNonTransient:
                logger.LogError(exception, "Non-transient failure; sending message to DLQ");
                await DeadLetterAsync(channel, delivery, message, exception);
                return;
            case DeliveryFailureAction.DeadLetterRetriesExhausted:
                logger.LogError(exception, "Transient failure and {MaxRetries} retries exhausted; sending message to DLQ", _options.MaxRetries);
                await DeadLetterAsync(channel, delivery, message, exception);
                return;
            default:
                logger.LogWarning(exception, "Transient failure; retry {Attempt}/{MaxRetries} in {Delay}s",
                    decision.NextRetryCount, _options.MaxRetries, decision.Delay.TotalSeconds);
                PublishDelayedOrRequeue(channel, delivery, decision.NextRetryCount, decision.Delay);
                return;
        }
    }

    /// <summary>Publishes a delayed copy and acks the original; if the publish fails the original is requeued (nothing is lost).</summary>
    private void PublishDelayedOrRequeue(IModel channel, BasicDeliverEventArgs delivery, int retryCount, TimeSpan delay)
    {
        try
        {
            PublishDelayed(channel, delivery, retryCount, delay);
        }
        catch (Exception publishError)
        {
            logger.LogError(publishError, "Could not publish the delayed copy; requeueing the original delivery");
            channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            return;
        }

        channel.BasicAck(delivery.DeliveryTag, multiple: false);
    }

    /// <summary>
    /// Publishes a copy straight into the retry queue (default exchange, routing key = queue name) with the given
    /// retry header and a per-message TTL. Confirmed before the original is acked.
    /// </summary>
    private void PublishDelayed(IModel channel, BasicDeliverEventArgs delivery, int retryCount, TimeSpan delay)
    {
        var original = delivery.BasicProperties;
        var props = RabbitMqTopology.CreateJobProperties(
            channel, original.MessageId ?? Guid.NewGuid().ToString("N"), ReadCorrelationId(original), retryCount);
        if (original.IsTimestampPresent()) props.Timestamp = original.Timestamp;
        props.Expiration = Math.Max(1L, (long)Math.Ceiling(delay.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
        CopyPublisherHeaders(original, props);

        channel.BasicPublish(exchange: string.Empty, routingKey: _options.RetryQueue, mandatory: false, props, delivery.Body);
        channel.WaitForConfirmsOrDie(PublishConfirmTimeout);
    }

    /// <summary>Keeps other publisher headers; the broker-managed x-death and the headers CreateJobProperties set are not copied.</summary>
    private static void CopyPublisherHeaders(IBasicProperties original, IBasicProperties copy)
    {
        if (original.Headers is null) return;
        foreach (var (key, value) in original.Headers)
        {
            if (key != BrokerDeathHeader) copy.Headers.TryAdd(key, value);
        }
    }

    /// <summary>Rejects without requeue; the queue's dead-letter exchange routes the message to the DLQ.</summary>
    private async Task DeadLetterAsync(IModel channel, BasicDeliverEventArgs delivery, CrawlJobRequested message, Exception exception)
    {
        // The exception type is enough for the UI; the full message (which may name internal hosts) stays in the logs.
        await TryMarkJobFailedAsync(message.JobId, $"Processing failed and the message was dead-lettered ({exception.GetType().Name}); see worker logs");
        channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
    }

    /// <summary>
    /// Best effort: a dead-lettered job must not stay Running forever in the UI. Conditional, so it never touches
    /// a terminal job or one whose live lease belongs to another worker.
    /// </summary>
    private async Task TryMarkJobFailedAsync(Guid jobId, string reason)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CrawlerDbContext>();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var owner = workerIdentity.Id;
            var failureReason = reason.Length <= MaxFailureReasonLength ? reason : reason[..MaxFailureReasonLength];

            var updated = await db.Jobs
                .Where(j => j.Id == jobId
                            && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
                            && (j.LeaseOwner == null || j.LeaseOwner == owner || j.LeaseExpiresAt < now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Failed)
                    .SetProperty(j => j.FailureReason, failureReason)
                    .SetProperty(j => j.CompletedAt, now)
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null), _stoppingToken);
            if (updated == 0) return;

            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, _stoppingToken);
            await progressNotifier.NotifyAsync(
                new JobProgressChanged(job.Id, JobStatus.Failed, job.PagesCrawled, job.PagesDiscovered, now), _stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mark job as Failed");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool TryDeserialize(ReadOnlyMemory<byte> body, out CrawlJobRequested message)
    {
        try
        {
            message = JsonSerializer.Deserialize<CrawlJobRequested>(body.Span, MessageSerialization.Options)!;
            return message is not null && message.JobId != Guid.Empty && !string.IsNullOrWhiteSpace(message.Url);
        }
        catch (JsonException)
        {
            message = null!;
            return false;
        }
    }

    private static string? ReadCorrelationId(IBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue(RabbitMqTopology.CorrelationIdHeader, out var value) == true)
        {
            var header = HeaderToString(value);
            if (!string.IsNullOrEmpty(header)) return header;
        }
        return properties.CorrelationId;
    }

    private static int ReadRetryCount(IBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue(RabbitMqTopology.RetryCountHeader, out var value) != true) return 0;
        return value switch
        {
            int i => i,
            long l => (int)l,
            byte b => b,
            short s => s,
            _ => int.TryParse(HeaderToString(value), out var parsed) ? parsed : 0
        };
    }

    // AMQP string headers arrive as byte[].
    private static string? HeaderToString(object? value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        null => null,
        _ => value.ToString()
    };
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Publisher with persistent delivery and publisher confirms. Holds one connection and one channel,
/// created lazily and re-created after a failure (so a broker outage at first use is not sticky).
/// Access is serialized because IModel is not thread-safe; the blocking client calls run on the thread
/// pool so a slow broker cannot pin request threads.
/// </summary>
public sealed class RabbitMqJobPublisher : IJobPublisher, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqJobPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqJobPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqJobPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync(CrawlJobRequested message, string correlationId, CancellationToken cancellationToken = default)
    {
        var body = MessageSerialization.Serialize(message);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => PublishCore(message, correlationId, body), cancellationToken);
            _logger.LogInformation("Published {RoutingKey} for job {JobId}", CrawlJobRequested.RoutingKey, message.JobId);
        }
        catch
        {
            ResetChannel();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PublishCore(CrawlJobRequested message, string correlationId, byte[] body)
    {
        var channel = GetOrCreateChannel();
        var props = RabbitMqTopology.CreateJobProperties(channel, message.JobId.ToString(), correlationId, retryCount: 0);
        channel.BasicPublish(_options.Exchange, CrawlJobRequested.RoutingKey, mandatory: false, props, body);
        channel.WaitForConfirmsOrDie(ConfirmTimeout);
    }

    private IModel GetOrCreateChannel()
    {
        if (_channel is { IsOpen: true }) return _channel;

        ResetChannel();
        _connection = RabbitMqTopology.CreateConnection(_options, "crawler-api", ConnectTimeout);
        _channel = _connection.CreateModel();
        RabbitMqTopology.Declare(_channel, _options);
        _channel.ConfirmSelect();
        return _channel;
    }

    private void ResetChannel()
    {
        try { _channel?.Dispose(); } catch { /* best effort */ }
        try { _connection?.Dispose(); } catch { /* best effort */ }
        _channel = null;
        _connection = null;
    }

    public void Dispose()
    {
        ResetChannel();
        _gate.Dispose();
    }
}

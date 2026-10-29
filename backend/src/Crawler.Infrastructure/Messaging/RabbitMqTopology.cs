using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Declares the exchange/queue layout. Idempotent — safe to call from both API and worker on startup.
///
///   crawl (direct) --[crawl.job.requested]--> crawl.jobs ----(nack, no requeue)----> crawl.dlx --> crawl.jobs.dlq
///                                                 ^
///   crawl.jobs.retry (no consumer) --(per-message TTL expires, dead-letters back to crawl)--┘
///
///   crawl.events (fanout) --> one exclusive queue per API instance (live progress for SSE clients)
/// </summary>
public static class RabbitMqTopology
{
    public const string RetryCountHeader = "x-retry-count";
    public const string CorrelationIdHeader = "x-correlation-id";

    public static void Declare(IModel channel, RabbitMqOptions options)
    {
        channel.ExchangeDeclare(options.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(options.DeadLetterQueue, options.DeadLetterExchange, CrawlJobRequested.RoutingKey);

        channel.ExchangeDeclare(options.Exchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(options.Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = options.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = CrawlJobRequested.RoutingKey
        });
        channel.QueueBind(options.Queue, options.Exchange, CrawlJobRequested.RoutingKey);

        // Delayed retry without blocking a consumer: the worker publishes the retry here with a per-message
        // `expiration`; when it expires the broker dead-letters it back onto the main queue.
        channel.QueueDeclare(options.RetryQueue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = options.Exchange,
            ["x-dead-letter-routing-key"] = CrawlJobRequested.RoutingKey
        });

        channel.ExchangeDeclare(options.EventsExchange, ExchangeType.Fanout, durable: true, autoDelete: false);
    }

    public static IConnection CreateConnection(RabbitMqOptions options, string clientName, TimeSpan? connectTimeout = null)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password,
            VirtualHost = options.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = clientName,
            RequestedConnectionTimeout = connectTimeout ?? TimeSpan.FromSeconds(10)
        };
        return factory.CreateConnection();
    }

    /// <summary>Standard properties for a <see cref="CrawlJobRequested"/> publish or re-publish.</summary>
    public static IBasicProperties CreateJobProperties(IModel channel, string messageId, string? correlationId, int retryCount)
    {
        var props = channel.CreateBasicProperties();
        props.Persistent = true;
        props.ContentType = "application/json";
        props.MessageId = messageId;
        props.CorrelationId = correlationId;
        props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        props.Headers = new Dictionary<string, object> { [RetryCountHeader] = retryCount };
        if (correlationId is not null)
            props.Headers[CorrelationIdHeader] = System.Text.Encoding.UTF8.GetBytes(correlationId);
        return props;
    }
}

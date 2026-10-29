namespace Crawler.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    public string Exchange { get; set; } = "crawl";
    public string Queue { get; set; } = "crawl.jobs";
    public string DeadLetterExchange { get; set; } = "crawl.dlx";
    public string DeadLetterQueue { get; set; } = "crawl.jobs.dlq";
    /// <summary>Holding queue for delayed retries; messages expire back into <see cref="Exchange"/>.</summary>
    public string RetryQueue { get; set; } = "crawl.jobs.retry";
    /// <summary>Fanout exchange for <see cref="JobProgressChanged"/> notifications.</summary>
    public string EventsExchange { get; set; } = JobProgressChanged.Exchange;

    /// <summary>How many times a message is re-published after a transient failure before it goes to the DLQ.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Base delay for exponential backoff between retries (delay = base * 2^attempt).</summary>
    public int RetryBaseDelaySeconds { get; set; } = 5;
    public ushort PrefetchCount { get; set; } = 1;

    public string AmqpUri => $"amqp://{Uri.EscapeDataString(UserName)}:{Uri.EscapeDataString(Password)}@{Host}:{Port}{(VirtualHost == "/" ? "/" : "/" + Uri.EscapeDataString(VirtualHost))}";
}

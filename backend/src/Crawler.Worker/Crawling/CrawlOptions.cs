namespace Crawler.Worker.Crawling;

public sealed class CrawlOptions
{
    public const string SectionName = "Crawl";

    /// <summary>Page budget used when a job does not specify a positive MaxPages.</summary>
    public int DefaultMaxPages { get; set; } = 200;

    /// <summary>Hard upper bound on a job's MaxDepth, regardless of what was requested.</summary>
    public int MaxDepthCap { get; set; } = 5;

    /// <summary>Pages of the same depth fetched in parallel.</summary>
    public int MaxConcurrentFetches { get; set; } = 4;

    /// <summary>Job progress counters are persisted (and the lease renewed) every N processed pages.</summary>
    public int ProgressFlushEveryPages { get; set; } = 5;

    /// <summary>The job status is re-read from the database every N processed pages to honour cancellation.</summary>
    public int CancellationCheckEveryPages { get; set; } = 5;

    /// <summary>
    /// How long a worker's claim on a job lasts without renewal. A redelivered message for a job with a live
    /// lease is deferred; an expired lease means the owner died and the job can be resumed by another worker.
    /// </summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>
    /// When false (default), links to other hosts count towards OutgoingLinkCount and the domain link ratio
    /// but are not stored as pages or edges, so the crawl stays on the starting domain.
    /// </summary>
    public bool FollowExternalLinks { get; set; }

    /// <summary>Cap on the outgoing-link list persisted per page; counts and the ratio still use every link.</summary>
    public int MaxStoredLinksPerPage { get; set; } = 1000;

    /// <summary>
    /// When false (default), connections to loopback, private, link-local, CGNAT, multicast and cloud metadata
    /// addresses are refused (SSRF guard, see <see cref="SafeAddressPolicy"/>). Enable only for local testing.
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }

    public string UserAgent { get; set; } = "CrawlerBot/1.0";

    public HttpFetchOptions Http { get; set; } = new();

    public bool IsValid()
        => DefaultMaxPages > 0 && MaxDepthCap >= 0 && MaxConcurrentFetches > 0
           && ProgressFlushEveryPages > 0 && CancellationCheckEveryPages > 0 && LeaseSeconds >= 10
           && MaxStoredLinksPerPage >= 0 && !string.IsNullOrWhiteSpace(UserAgent) && Http.IsValid();
}

/// <summary>Outbound HTTP settings (resilience pipeline and connection handler), applied per target authority.</summary>
public sealed class HttpFetchOptions
{
    public int TotalRequestTimeoutSeconds { get; set; } = 30;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 3;
    public int MaxRedirects { get; set; } = 5;
    public int MaxConnectionsPerServer { get; set; } = 4;

    public bool IsValid()
        => AttemptTimeoutSeconds > 0 && TotalRequestTimeoutSeconds > AttemptTimeoutSeconds
           && MaxRetries >= 0 && MaxRedirects >= 0 && MaxConnectionsPerServer > 0;
}

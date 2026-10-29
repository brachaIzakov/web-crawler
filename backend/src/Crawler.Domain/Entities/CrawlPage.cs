namespace Crawler.Domain.Entities;

public enum PageStatus
{
    /// <summary>Discovered as a link but not yet fetched (or outside depth/page limits).</summary>
    Discovered = 0,
    Crawled = 1,
    /// <summary>Fetch failed or the response was not HTML.</summary>
    Failed = 2,
    Skipped = 3
}

public class CrawlPage
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public CrawlJob Job { get; set; } = null!;

    /// <summary>Absolute, normalized URL. Unique within a job (idempotency key).</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Normalized URL the server redirected to, when it differs from <see cref="Url"/>. Links are resolved against it.</summary>
    public string? FinalUrl { get; set; }
    public int Depth { get; set; }
    /// <summary>Page that first discovered this URL (BFS tree parent). Null for the root.</summary>
    public long? ParentPageId { get; set; }
    public CrawlPage? ParentPage { get; set; }

    public PageStatus Status { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? Title { get; set; }
    public string? Error { get; set; }

    public int OutgoingLinkCount { get; set; }
    public int InternalLinkCount { get; set; }
    /// <summary>InternalLinkCount / OutgoingLinkCount; 0 when the page has no outgoing links.</summary>
    public double DomainLinkRatio { get; set; }

    /// <summary>
    /// Every outgoing link discovered on the page (absolute, normalized, deduplicated), including external
    /// ones and ones never stored as pages. This is the input to the counts/ratio above; the edges table only
    /// holds links between stored pages.
    /// </summary>
    public List<string> OutgoingLinks { get; set; } = new();

    public DateTime? CrawledAt { get; set; }
}

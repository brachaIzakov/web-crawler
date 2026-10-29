namespace Crawler.Domain.Entities;

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Canceled = 4
}

public class CrawlJob
{
    public Guid Id { get; set; }
    public string InputUrl { get; set; } = string.Empty;
    /// <summary>Host of the input URL; used for the Domain Link Ratio of every page in the job.</summary>
    public string StartingDomain { get; set; } = string.Empty;
    public int MaxDepth { get; set; }
    public int MaxPages { get; set; }
    public JobStatus Status { get; set; }
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Worker instance currently processing the job and when its claim expires. A redelivered message for a
    /// job whose lease is still held is deferred instead of crawled twice; an expired lease means the worker died.
    /// </summary>
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>Pages fetched and parsed so far (progress counter).</summary>
    public int PagesCrawled { get; set; }
    /// <summary>Unique normalized URLs discovered so far (upper bound for progress).</summary>
    public int PagesDiscovered { get; set; }

    public ICollection<CrawlPage> Pages { get; set; } = new List<CrawlPage>();
    public ICollection<CrawlEdge> Edges { get; set; } = new List<CrawlEdge>();

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;
}

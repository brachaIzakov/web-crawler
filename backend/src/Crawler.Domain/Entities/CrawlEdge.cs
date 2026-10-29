namespace Crawler.Domain.Entities;

/// <summary>
/// A directed link parent → child discovered during a crawl. The full link graph lives here;
/// the hierarchical tree is derived from <see cref="CrawlPage.ParentPageId"/>.
/// </summary>
public class CrawlEdge
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public CrawlJob Job { get; set; } = null!;

    public long ParentPageId { get; set; }
    public CrawlPage ParentPage { get; set; } = null!;

    public long ChildPageId { get; set; }
    public CrawlPage ChildPage { get; set; } = null!;
}

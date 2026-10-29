using Crawler.Domain.Entities;

namespace Crawler.Api.Tests;

internal static class Seed
{
    public static CrawlJob Job(
        JobStatus status = JobStatus.Pending,
        string url = "https://example.com/",
        DateTime? createdAt = null,
        int crawled = 0,
        int discovered = 0,
        int maxPages = 200,
        int maxDepth = 2,
        DateTime? startedAt = null,
        DateTime? completedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        InputUrl = url,
        StartingDomain = new Uri(url).Host,
        MaxDepth = maxDepth,
        MaxPages = maxPages,
        Status = status,
        CreatedAt = createdAt ?? DateTime.UtcNow,
        StartedAt = startedAt,
        CompletedAt = completedAt,
        PagesCrawled = crawled,
        PagesDiscovered = discovered
    };

    public static CrawlPage Page(
        Guid jobId, long id, string url, int depth, long? parentId = null,
        params string[] outgoingLinks) => new()
    {
        Id = id,
        JobId = jobId,
        Url = url,
        Depth = depth,
        ParentPageId = parentId,
        Status = PageStatus.Crawled,
        HttpStatusCode = 200,
        Title = $"Title {id}",
        OutgoingLinks = outgoingLinks.ToList(),
        OutgoingLinkCount = outgoingLinks.Length,
        CrawledAt = DateTime.UtcNow
    };
}

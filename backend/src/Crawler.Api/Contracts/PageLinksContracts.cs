namespace Crawler.Api.Contracts;

public sealed record PageLink(string Url, bool IsInternal);

public sealed record PageLinksResponse(
    long PageId,
    string Url,
    int OutgoingLinkCount,
    int InternalLinkCount,
    double DomainLinkRatio,
    IReadOnlyList<PageLink> Links);

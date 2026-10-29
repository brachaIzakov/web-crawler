using Crawler.Domain.Entities;

namespace Crawler.Api.Contracts;

public sealed record JobTreeResponse(Guid JobId, int TotalPages, TreeNode? Root);

public sealed record TreeNode(
    long PageId,
    string Url,
    string? FinalUrl,
    int Depth,
    PageStatus Status,
    int? HttpStatusCode,
    string? Title,
    int OutgoingLinkCount,
    int InternalLinkCount,
    double DomainLinkRatio,
    string? Error)
{
    public List<TreeNode> Children { get; } = new();
}

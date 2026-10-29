using Crawler.Api.Contracts;
using Crawler.Domain.Entities;

namespace Crawler.Api.Services;

/// <summary>The columns the tree needs — deliberately excludes the per-page link list (see GetPageLinksAsync).</summary>
public sealed record TreePageRow(
    long Id,
    long? ParentPageId,
    string Url,
    string? FinalUrl,
    int Depth,
    PageStatus Status,
    int? HttpStatusCode,
    string? Title,
    int OutgoingLinkCount,
    int InternalLinkCount,
    double DomainLinkRatio,
    string? Error);

public static class TreeBuilder
{
    /// <summary>
    /// Builds the BFS tree in O(n). Rows must be ordered by depth, so every parent is indexed
    /// before its children and children keep discovery order. Rows whose parent is missing are dropped.
    /// </summary>
    public static TreeNode? Build(IEnumerable<TreePageRow> rowsOrderedByDepth)
    {
        var nodesById = new Dictionary<long, TreeNode>();
        TreeNode? root = null;

        foreach (var row in rowsOrderedByDepth)
        {
            var node = ToNode(row);
            nodesById[row.Id] = node;

            if (row.ParentPageId is null)
                root ??= node;
            else if (nodesById.TryGetValue(row.ParentPageId.Value, out var parent))
                parent.Children.Add(node);
        }

        return root;
    }

    public static TreeNode? Build(IEnumerable<CrawlPage> pagesOrderedByDepth)
        => Build(pagesOrderedByDepth.Select(ToRow));

    public static TreePageRow ToRow(CrawlPage p) => new(
        p.Id, p.ParentPageId, p.Url, p.FinalUrl, p.Depth, p.Status, p.HttpStatusCode, p.Title,
        p.OutgoingLinkCount, p.InternalLinkCount, p.DomainLinkRatio, p.Error);

    private static TreeNode ToNode(TreePageRow row) => new(
        row.Id,
        row.Url,
        row.FinalUrl,
        row.Depth,
        row.Status,
        row.HttpStatusCode,
        row.Title,
        row.OutgoingLinkCount,
        row.InternalLinkCount,
        row.DomainLinkRatio,
        row.Error);
}

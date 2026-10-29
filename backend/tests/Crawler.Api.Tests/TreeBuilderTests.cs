using Crawler.Api.Services;
using Crawler.Domain.Entities;
using FluentAssertions;

namespace Crawler.Api.Tests;

public class TreeBuilderTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    private static CrawlPage P(long id, int depth, long? parent) => Seed.Page(JobId, id, $"https://example.com/p{id}", depth, parent);

    [Fact]
    public void Build_NoPages_ReturnsNull()
        => TreeBuilder.Build(Array.Empty<CrawlPage>()).Should().BeNull();

    [Fact]
    public void Build_SingleRoot_ReturnsLeafRoot()
    {
        var root = TreeBuilder.Build([P(1, 0, null)]);
        root!.PageId.Should().Be(1);
        root.Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_NestsChildrenUnderParents_PreservingInputOrder()
    {
        var root = TreeBuilder.Build([P(1, 0, null), P(2, 1, 1), P(3, 1, 1), P(4, 2, 3), P(5, 2, 2), P(6, 2, 3)])!;

        root.Children.Select(c => c.PageId).Should().Equal(2, 3);
        root.Children[0].Children.Select(c => c.PageId).Should().Equal(5);
        root.Children[1].Children.Select(c => c.PageId).Should().Equal(4, 6);
    }

    [Fact]
    public void Build_ChildrenOrder_FollowsInputOrderNotId()
    {
        // TreeBuilder trusts the caller's ordering (the service orders by Depth, then Id).
        var root = TreeBuilder.Build([P(1, 0, null), P(9, 1, 1), P(2, 1, 1)])!;
        root.Children.Select(c => c.PageId).Should().Equal(9, 2);
    }

    [Fact]
    public void Build_OrphanWhoseParentIsMissing_IsDroppedWithoutThrowing()
    {
        var pages = new[] { P(1, 0, null), P(2, 1, 1), P(3, 2, 999), P(4, 3, 3) };

        var act = () => TreeBuilder.Build(pages);

        var root = act.Should().NotThrow().Subject!;
        root.Children.Select(c => c.PageId).Should().Equal(2);
        root.Children[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_ChildBeforeParentInInput_IsDropped()
    {
        // Documents the precondition: input must be ordered by depth.
        var root = TreeBuilder.Build([P(1, 0, null), P(3, 2, 2), P(2, 1, 1)])!;
        root.Children.Select(c => c.PageId).Should().Equal(2);
        root.Children[0].Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_TwoRoots_FirstWins()
    {
        var root = TreeBuilder.Build([P(1, 0, null), P(2, 0, null), P(3, 1, 2)])!;
        root.PageId.Should().Be(1);
        root.Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_NoRootOnlyChildren_ReturnsNull()
        => TreeBuilder.Build([P(2, 1, 1), P(3, 1, 1)]).Should().BeNull();

    [Fact]
    public void Build_CopiesPageFieldsToNode()
    {
        var page = P(1, 0, null);
        page.FinalUrl = "https://example.com/final";
        page.Status = PageStatus.Failed;
        page.HttpStatusCode = 500;
        page.Error = "HTTP 500";
        page.OutgoingLinkCount = 3;
        page.InternalLinkCount = 1;
        page.DomainLinkRatio = 1d / 3d;

        var node = TreeBuilder.Build([page])!;

        node.Url.Should().Be("https://example.com/p1");
        node.FinalUrl.Should().Be("https://example.com/final");
        node.Depth.Should().Be(0);
        node.Status.Should().Be(PageStatus.Failed);
        node.HttpStatusCode.Should().Be(500);
        node.Title.Should().Be("Title 1");
        node.Error.Should().Be("HTTP 500");
        node.OutgoingLinkCount.Should().Be(3);
        node.InternalLinkCount.Should().Be(1);
        node.DomainLinkRatio.Should().BeApproximately(1d / 3d, 1e-12);
    }

    [Fact]
    public void Build_LargeWideTree_IsLinear()
    {
        var pages = new List<CrawlPage> { P(1, 0, null) };
        for (var i = 2; i <= 1001; i++) pages.Add(P(i, 1, 1));
        for (var i = 1002; i <= 5001; i++) pages.Add(P(i, 2, 2 + (i % 1000)));

        var root = TreeBuilder.Build(pages)!;

        root.Children.Should().HaveCount(1000);
        root.Children.Sum(c => c.Children.Count).Should().Be(4000);
    }
}

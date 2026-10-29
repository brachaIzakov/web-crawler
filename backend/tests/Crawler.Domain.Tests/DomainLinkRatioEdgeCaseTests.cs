using Crawler.Domain.Crawling;
using FluentAssertions;

namespace Crawler.Domain.Tests;

public class DomainLinkRatioEdgeCaseTests
{
    private const string Domain = "example.com";

    [Fact]
    public void Compute_OneThird_IsExactDoubleDivision()
    {
        var links = new[] { "https://example.com/", "https://a.org/", "https://b.org/" }.Select(u => new Uri(u)).ToList();

        var ratio = DomainLinkRatio.Compute(links, Domain);

        ratio.Should().Be(1d / 3d);
        ratio.Should().NotBe(0d, "integer division would truncate to 0");
    }

    [Theory]
    [InlineData(1000, 0, 0d)]
    [InlineData(1000, 1, 0.001)]
    [InlineData(1000, 333, 0.333)]
    [InlineData(1000, 999, 0.999)]
    [InlineData(1000, 1000, 1d)]
    public void Compute_1000Links_IsPrecise(int total, int internalCount, double expected)
    {
        var links = Enumerable.Range(0, total)
            .Select(i => new Uri(i < internalCount ? $"https://example.com/p{i}" : $"https://ext{i}.org/"))
            .ToList();

        DomainLinkRatio.Compute(links, Domain).Should().BeApproximately(expected, 1e-12);
        DomainLinkRatio.CountInternal(links, Domain).Should().Be(internalCount);
    }

    [Theory]
    [InlineData(0, 0, 0d)]
    [InlineData(3, 1, 1d / 3d)]
    [InlineData(4, 4, 1d)]
    [InlineData(1000, 1, 0.001)]
    [InlineData(5, 0, 0d)]
    public void Compute_FromCounts_MatchesDefinition(int outgoing, int @internal, double expected)
        => DomainLinkRatio.Compute(outgoing, @internal).Should().Be(expected);

    [Fact]
    public void Compute_FromCounts_AgreesWithComputeFromLinks()
    {
        var links = new[] { "https://example.com/a", "https://EXAMPLE.com/b", "https://x.org/", "https://y.org/", "https://z.org/" }
            .Select(u => new Uri(u)).ToList();
        var internalCount = DomainLinkRatio.CountInternal(links, Domain);

        DomainLinkRatio.Compute(links.Count, internalCount).Should().Be(DomainLinkRatio.Compute(links, Domain));
    }

    [Fact]
    public void Compute_ResultIsAlwaysWithinZeroAndOne()
    {
        var random = new Random(42);
        for (var run = 0; run < 50; run++)
        {
            var links = Enumerable.Range(0, random.Next(1, 200))
                .Select(i => new Uri(random.Next(2) == 0 ? $"https://example.com/{i}" : $"https://x{i}.org/"))
                .ToList();
            DomainLinkRatio.Compute(links, Domain).Should().BeInRange(0d, 1d);
        }
    }

    [Theory]
    [InlineData("https://bücher.example/a", "bücher.example")]
    [InlineData("https://bücher.example/a", "xn--bcher-kva.example")]
    [InlineData("https://xn--bcher-kva.example/a", "bücher.example")]
    [InlineData("https://XN--BCHER-KVA.example/a", "BÜCHER.example")]
    [InlineData("https://BÜCHER.example/a", "xn--bcher-kva.example")]
    public void IsInternal_IdnHost_MatchesAcrossUnicodeAndPunycodeSpellings(string link, string startingDomain)
        => DomainLinkRatio.IsInternal(new Uri(link), startingDomain).Should().BeTrue();

    [Fact]
    public void IsInternal_DifferentIdnHost_IsExternal()
        => DomainLinkRatio.IsInternal(new Uri("https://bucher.example/"), "bücher.example").Should().BeFalse();

    [Fact]
    public void IsInternal_TrailingDotHost_IsExternal()
        => DomainLinkRatio.IsInternal(new Uri("https://example.com./"), Domain).Should().BeFalse();

    [Fact]
    public void IsInternal_InvalidStartingDomain_DoesNotThrow()
    {
        var act = () => DomainLinkRatio.IsInternal(new Uri("https://example.com/"), "bad..domain");
        act.Should().NotThrow().Which.Should().BeFalse();
    }
}

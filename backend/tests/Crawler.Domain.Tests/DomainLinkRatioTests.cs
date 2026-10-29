using Crawler.Domain.Crawling;
using FluentAssertions;

namespace Crawler.Domain.Tests;

public class DomainLinkRatioTests
{
    private const string Domain = "example.com";

    private static IReadOnlyCollection<Uri> Links(params string[] urls) => urls.Select(u => new Uri(u)).ToList();

    [Fact]
    public void Compute_ZeroLinks_ReturnsZero()
        => DomainLinkRatio.Compute(Array.Empty<Uri>(), Domain).Should().Be(0d);

    [Fact]
    public void Compute_AllInternal_ReturnsOne()
        => DomainLinkRatio.Compute(Links("https://example.com/a", "https://example.com/b"), Domain).Should().Be(1d);

    [Fact]
    public void Compute_AllExternal_ReturnsZero()
        => DomainLinkRatio.Compute(Links("https://other.org/a", "https://third.net/"), Domain).Should().Be(0d);

    [Fact]
    public void Compute_TwoOfFourInternal_ReturnsHalf()
        => DomainLinkRatio.Compute(
            Links("https://example.com/a", "https://example.com/b", "https://other.org/", "https://third.net/"),
            Domain).Should().Be(0.5d);

    [Fact]
    public void Compute_OneOfThreeInternal_ReturnsOneThird()
        => DomainLinkRatio.Compute(
            Links("https://example.com/a", "https://other.org/", "https://third.net/"),
            Domain).Should().BeApproximately(1d / 3d, 1e-9);

    [Theory]
    [InlineData("https://EXAMPLE.com/a")]
    [InlineData("https://Example.Com/a")]
    public void IsInternal_HostCaseDiffers_IsInternal(string url)
        => DomainLinkRatio.IsInternal(new Uri(url), Domain).Should().BeTrue();

    [Fact]
    public void IsInternal_StartingDomainUppercase_IsInternal()
        => DomainLinkRatio.IsInternal(new Uri("https://example.com/"), "EXAMPLE.COM").Should().BeTrue();

    [Theory]
    [InlineData("https://www.example.com/")]
    [InlineData("https://blog.example.com/")]
    [InlineData("https://example.com.evil.org/")]
    [InlineData("https://notexample.com/")]
    public void IsInternal_SubdomainOrLookalike_IsExternal(string url)
        => DomainLinkRatio.IsInternal(new Uri(url), Domain).Should().BeFalse();

    [Fact]
    public void IsInternal_StartingDomainIsWww_ApexIsExternal()
        => DomainLinkRatio.IsInternal(new Uri("https://example.com/"), "www.example.com").Should().BeFalse();

    [Theory]
    [InlineData("http://example.com/a")]
    [InlineData("https://example.com/a")]
    [InlineData("https://example.com:8443/a")]
    [InlineData("http://example.com:8080/a?x=1#f")]
    public void IsInternal_SchemeAndPortIgnored_IsInternal(string url)
        => DomainLinkRatio.IsInternal(new Uri(url), Domain).Should().BeTrue();

    [Fact]
    public void CountInternal_MixedLinks_CountsOnlyMatchingHosts()
        => DomainLinkRatio.CountInternal(
            Links("https://example.com/a", "http://EXAMPLE.com:81/b", "https://www.example.com/", "https://x.org/"),
            Domain).Should().Be(2);

    [Fact]
    public void CountInternal_Empty_ReturnsZero()
        => DomainLinkRatio.CountInternal(Array.Empty<Uri>(), Domain).Should().Be(0);

    [Fact]
    public void Compute_CaseAndPortVariants_CountAsInternal()
        => DomainLinkRatio.Compute(
            Links("http://Example.COM/", "https://example.com:444/", "https://sub.example.com/", "https://x.org/"),
            Domain).Should().Be(0.5d);

    [Theory]
    [InlineData("example.com")]
    [InlineData("EXAMPLE.com")]
    [InlineData("bücher.example")]
    [InlineData("xn--bcher-kva.example")]
    public void IsInternalTo_WithCanonicalizedDomain_MatchesIsInternal(string startingDomain)
    {
        var links = Links("https://example.com/a", "http://EXAMPLE.COM:8080/b", "https://bücher.example/c",
            "https://xn--bcher-kva.example/d", "https://other.org/", "https://sub.example.com/");
        var canonical = DomainLinkRatio.Canonicalize(startingDomain);

        // Reference: the original per-link comparison of lower-cased punycode hosts.
        var expected = links.Select(l => UrlNormalizer.CanonicalHost(l) == canonical).ToList();
        expected.Count(e => e).Should().BeGreaterThan(0);
        links.Select(l => DomainLinkRatio.IsInternalTo(l, canonical)).Should().Equal(expected);
        links.Select(l => DomainLinkRatio.IsInternal(l, startingDomain)).Should().Equal(expected);
        DomainLinkRatio.CountInternalTo(links, canonical).Should().Be(DomainLinkRatio.CountInternal(links, startingDomain));
    }
}

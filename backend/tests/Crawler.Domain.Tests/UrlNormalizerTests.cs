using Crawler.Domain.Crawling;
using FluentAssertions;

namespace Crawler.Domain.Tests;

public class UrlNormalizerTests
{
    private static readonly Uri Base = new("https://example.com/dir/page.html?x=1#top");

    private static string? N(string? candidate, Uri? baseUri = null)
        => UrlNormalizer.TryNormalize(candidate, baseUri, out var u) ? u.ToString() : null;

    [Theory]
    [InlineData("about.html", "https://example.com/dir/about.html")]
    [InlineData("../x", "https://example.com/x")]
    [InlineData("/abs", "https://example.com/abs")]
    [InlineData("/abs/deep/path", "https://example.com/abs/deep/path")]
    [InlineData("?q=1", "https://example.com/dir/page.html?q=1")]
    [InlineData("sub/", "https://example.com/dir/sub/")]
    public void TryNormalize_RelativeLink_ResolvesAgainstBase(string input, string expected)
        => N(input, Base).Should().Be(expected);

    [Fact]
    public void TryNormalize_FragmentOnly_ReturnsSamePageWithoutFragment()
        => N("#frag", Base).Should().Be("https://example.com/dir/page.html?x=1");

    [Theory]
    [InlineData("https://example.com/a#section", "https://example.com/a")]
    [InlineData("https://example.com/a?x=1#section", "https://example.com/a?x=1")]
    [InlineData("https://example.com/#top", "https://example.com/")]
    [InlineData("https://example.com/a#", "https://example.com/a")]
    public void TryNormalize_Fragment_IsRemoved(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("HTTPS://EXAMPLE.COM/", "https://example.com/")]
    [InlineData("https://Example.Com/Path", "https://example.com/Path")]
    [InlineData("https://example.com/UPPER/Case", "https://example.com/UPPER/Case")]
    [InlineData("HTTP://WWW.Example.COM/A?Q=B", "http://www.example.com/A?Q=B")]
    public void TryNormalize_Casing_LowercasesSchemeAndHostButNotPathOrQuery(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")]
    [InlineData("http://example.com:8080/a", "http://example.com:8080/a")]
    [InlineData("http://example.com:443/a", "http://example.com:443/a")]
    [InlineData("https://example.com:80/a", "https://example.com:80/a")]
    public void TryNormalize_Port_DefaultStrippedNonDefaultKept(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("https://example.com/a/", "https://example.com/a/")]
    [InlineData("https://example.com/a/b/", "https://example.com/a/b/")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("https://example.com/a/?x=1", "https://example.com/a/?x=1")]
    public void TryNormalize_TrailingSlash_IsKeptAndEmptyPathBecomesRoot(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("https://example.com/a?x=1&y=2", "https://example.com/a?x=1&y=2")]
    [InlineData("https://example.com/?q=hello", "https://example.com/?q=hello")]
    [InlineData("https://example.com/a?b=C", "https://example.com/a?b=C")]
    public void TryNormalize_Query_IsPreserved(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("https://example.com/a?", "https://example.com/a")]
    [InlineData("https://example.com/?", "https://example.com/")]
    [InlineData("https://example.com/a?#frag", "https://example.com/a")]
    public void TryNormalize_EmptyQuery_IsDropped(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("MAILTO:someone@example.com")]
    [InlineData("tel:+123456789")]
    [InlineData("javascript:void(0)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///c:/x.txt")]
    public void TryNormalize_NonHttpScheme_IsRejected(string input)
    {
        UrlNormalizer.TryNormalize(input, Base, out _).Should().BeFalse();
        UrlNormalizer.TryNormalize(input, null, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("  https://example.com/a  ", "https://example.com/a")]
    [InlineData("\thttps://example.com/a\n", "https://example.com/a")]
    public void TryNormalize_Whitespace_IsTrimmed(string input, string expected)
        => N(input).Should().Be(expected);

    [Fact]
    public void TryNormalize_WhitespaceAroundRelative_IsTrimmed()
        => N("  about.html ", Base).Should().Be("https://example.com/dir/about.html");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void TryNormalize_NullOrBlank_IsRejected(string? input)
    {
        UrlNormalizer.TryNormalize(input, Base, out var result).Should().BeFalse();
        result.Should().BeNull();
        UrlNormalizer.TryNormalize(input, null, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("https://")]
    [InlineData("not a url at all")]
    [InlineData("http://exa mple.com/")]
    public void TryNormalize_InvalidUrl_IsRejected(string input)
        => UrlNormalizer.TryNormalize(input, null, out _).Should().BeFalse();

    [Fact]
    public void TryNormalize_AbsoluteUrlWithNullBase_Succeeds()
        => N("https://example.com/a", null).Should().Be("https://example.com/a");

    [Theory]
    [InlineData("about.html")]
    [InlineData("/abs")]
    [InlineData("../x")]
    [InlineData("?q=1")]
    [InlineData("#frag")]
    public void TryNormalize_RelativeWithNullBase_IsRejected(string input)
        => UrlNormalizer.TryNormalize(input, null, out _).Should().BeFalse();

    [Fact]
    public void TryNormalize_AbsoluteUrlWithBase_IgnoresBase()
        => N("http://other.org/z", Base).Should().Be("http://other.org/z");

    [Theory]
    [InlineData("//cdn.example.org/x.js", "https://cdn.example.org/x.js")]
    [InlineData("//Other.COM/a/", "https://other.com/a/")]
    public void TryNormalize_ProtocolRelative_UsesBaseScheme(string input, string expected)
        => N(input, Base).Should().Be(expected);

    [Fact]
    public void TryNormalize_ProtocolRelativeWithHttpBase_UsesHttp()
        => N("//cdn.example.org/x", new Uri("http://example.com/")).Should().Be("http://cdn.example.org/x");

    [Fact]
    public void Normalize_ValidAbsolute_ReturnsNormalizedString()
        => UrlNormalizer.Normalize("HTTPS://Example.com:443/a/#x").Should().Be("https://example.com/a/");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("mailto:a@b.com")]
    [InlineData("ftp://example.com/")]
    [InlineData("garbage")]
    public void Normalize_InvalidInput_ThrowsArgumentException(string input)
    {
        var act = () => UrlNormalizer.Normalize(input);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("HTTPS://Example.com:443/A/B/?q=1#frag")]
    [InlineData("http://example.com:80")]
    [InlineData("https://example.com/a/?")]
    [InlineData("https://example.com:8443/a/b/")]
    [InlineData("https://example.com/")]
    public void Normalize_Twice_IsIdempotent(string input)
    {
        var once = UrlNormalizer.Normalize(input);
        var twice = UrlNormalizer.Normalize(once);
        twice.Should().Be(once);
    }
}

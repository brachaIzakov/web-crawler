using Crawler.Domain.Crawling;
using FluentAssertions;

namespace Crawler.Domain.Tests;

/// <summary>
/// Edge cases. Where behaviour is a judgement call the test documents what the normalizer actually does,
/// so a change in System.Uri or in the rules shows up as a failing test rather than a silent drift.
/// </summary>
public class UrlNormalizerEdgeCaseTests
{
    private static readonly Uri HttpsBase = new("https://example.com/dir/page.html");

    private static string? N(string? candidate, Uri? baseUri = null)
        => UrlNormalizer.TryNormalize(candidate, baseUri, out var u) ? u.AbsoluteUri : null;

    // ---- scheme / host / port -------------------------------------------------------------------

    [Theory]
    [InlineData("HTTP://example.com/a", "http://example.com/a")]
    [InlineData("hTtPs://example.com/a", "https://example.com/a")]
    [InlineData("HTTP://Example.COM/a/#x", "http://example.com/a/")]
    public void UppercaseScheme_IsLowercased(string input, string expected)
        => N(input).Should().Be(expected);

    [Fact]
    public void UppercaseScheme_RelativeToBase_IsAccepted()
        => N("HTTPS://other.org/x", HttpsBase).Should().Be("https://other.org/x");

    [Theory]
    [InlineData("http://example.com:8080/", "http://example.com:8080/")]
    [InlineData("https://host:443/", "https://host/")]
    [InlineData("http://host:80", "http://host/")]
    public void Port_NonDefaultKept_DefaultDropped(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("https://bücher.example/")]
    [InlineData("https://BÜCHER.example/")]
    [InlineData("https://xn--bcher-kva.example/")]
    [InlineData("https://XN--BCHER-KVA.EXAMPLE/")]
    public void IdnHost_AllSpellings_NormalizeToLowercasePunycode(string input)
    {
        UrlNormalizer.TryNormalize(input, null, out var uri).Should().BeTrue();

        uri.Host.Should().Be("xn--bcher-kva.example");
        uri.AbsoluteUri.Should().Be("https://xn--bcher-kva.example/");
        UrlNormalizer.Normalize(input).Should().Be("https://xn--bcher-kva.example/");
    }

    [Theory]
    [InlineData("https://Example.COM/", "example.com")]
    [InlineData("https://bücher.example/", "xn--bcher-kva.example")]
    [InlineData("https://BÜCHER.example/", "xn--bcher-kva.example")]
    [InlineData("http://127.0.0.1:8080/", "127.0.0.1")]
    public void CanonicalHost_IsLowercasePunycode(string url, string expected)
        => UrlNormalizer.CanonicalHost(new Uri(url)).Should().Be(expected);

    [Fact]
    public void Ipv4Host_IsAccepted()
        => N("http://127.0.0.1:8080/x/").Should().Be("http://127.0.0.1:8080/x/");

    [Fact]
    public void Ipv6Host_IsAccepted()
        => N("http://[::1]:8080/x").Should().Be("http://[::1]:8080/x");

    [Fact]
    public void TrailingDotHost_IsKeptAsDistinctHost()
        => N("https://example.com./a").Should().Be("https://example.com./a");

    // ---- userinfo -------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://user:pw@Host.example/a/", "http://host.example/a/")]
    [InlineData("http://user@host.example/", "http://host.example/")]
    [InlineData("https://:pw@host.example:8443/x", "https://host.example:8443/x")]
    public void Userinfo_IsStripped(string input, string expected)
    {
        UrlNormalizer.TryNormalize(input, null, out var uri).Should().BeTrue();
        uri.AbsoluteUri.Should().Be(expected);
        uri.UserInfo.Should().BeEmpty();
    }

    // ---- trailing slash ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/a/", "https://example.com/a/")]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/a/?x=1", "https://example.com/a/?x=1")]
    [InlineData("https://example.com/a///", "https://example.com/a///")]
    [InlineData("https://example.com//double//slash//", "https://example.com//double//slash//")]
    public void TrailingAndRepeatedSlashes_ArePreserved(string input, string expected)
        => N(input).Should().Be(expected);

    [Fact]
    public void TrailingSlashVariants_AreDistinctIdentities()
        => N("https://example.com/docs").Should().NotBe(N("https://example.com/docs/"));

    // ---- encoding ---------------------------------------------------------------------------------

    [Fact]
    public void PathWithSpaces_IsPercentEncoded()
    {
        N("https://example.com/a b/c d.html").Should().Be("https://example.com/a%20b/c%20d.html");
        UrlNormalizer.Normalize("https://example.com/a b").Should().Be("https://example.com/a%20b");
    }

    [Fact]
    public void RelativePathWithSpaces_ResolvesAndEncodes()
        => N("my file.html", HttpsBase).Should().Be("https://example.com/dir/my%20file.html");

    [Fact]
    public void EncodedTilde_IsDecodedToTilde_SoBothFormsCollapse()
    {
        // System.Uri unescapes unreserved characters, so %7E and ~ normalize to the same URL.
        N("https://example.com/%7Euser").Should().Be("https://example.com/~user");
        N("https://example.com/~user").Should().Be("https://example.com/~user");
    }

    [Fact]
    public void EncodedSlash_IsPreserved()
        => N("https://example.com/a%2Fb").Should().Be("https://example.com/a%2Fb");

    [Theory]
    [InlineData("https://example.com/a%2fb", "https://example.com/a%2Fb")]
    [InlineData("https://example.com/a%2fb?x=%3d%3D", "https://example.com/a%2Fb?x=%3D%3D")]
    [InlineData("https://example.com/caf%c3%a9", "https://example.com/caf%C3%A9")]
    public void LowercasePercentEncoding_IsUppercased_SoBothSpellingsCollapse(string input, string expected)
        => N(input).Should().Be(expected);

    [Fact]
    public void LinksDifferingOnlyInEscapeCase_AreOneOutgoingLink()
    {
        var parsed = LinkExtractor.Parse("""<a href="/a%2fb">1</a><a href="/a%2Fb">2</a>""", HttpsBase);
        parsed.Links.Should().ContainSingle().Which.AbsoluteUri.Should().Be("https://example.com/a%2Fb");
    }

    // ---- query / fragment -----------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/s?b=2&a=1&c=3", "https://example.com/s?b=2&a=1&c=3")]
    [InlineData("https://example.com/s?a=1&a=2", "https://example.com/s?a=1&a=2")]
    [InlineData("https://example.com/s?a=&b", "https://example.com/s?a=&b")]
    public void Query_ParameterOrderAndDuplicates_ArePreserved(string input, string expected)
        => N(input).Should().Be(expected);

    [Theory]
    [InlineData("https://example.com/?a=1#frag", "https://example.com/?a=1")]
    [InlineData("https://example.com/p/?a=1#frag", "https://example.com/p/?a=1")]
    [InlineData("https://example.com/p#frag?notquery", "https://example.com/p")]
    public void QueryWithFragment_FragmentDroppedQueryKept(string input, string expected)
        => N(input).Should().Be(expected);

    // ---- protocol-relative ----------------------------------------------------------------------

    [Theory]
    [InlineData("//cdn.example.com/x", "https://cdn.example.com/x")]
    [InlineData("//CDN.Example.com:443/x/", "https://cdn.example.com/x/")]
    [InlineData("//cdn.example.com:8443/x", "https://cdn.example.com:8443/x")]
    public void ProtocolRelative_WithHttpsBase_UsesHttps(string input, string expected)
        => N(input, HttpsBase).Should().Be(expected);

    [Fact]
    public void ProtocolRelative_WithoutBase_IsRejected()
        => N("//cdn.example.com/x").Should().BeNull();

    // ---- whitespace -----------------------------------------------------------------------------

    [Theory]
    [InlineData("https://exa\tmple.com/a", "https://example.com/a")]
    [InlineData("https://example.com/a\tb", "https://example.com/ab")]
    [InlineData("https://example.com/a\r\nb", "https://example.com/ab")]
    [InlineData("ht\ntps://example.com/", "https://example.com/")]
    [InlineData("\r\n https://example.com/a \r\n", "https://example.com/a")]
    public void TabCrLf_AnywhereInUrl_AreRemoved(string input, string expected)
        => N(input).Should().Be(expected);

    [Fact]
    public void TabInsideRelativeLink_IsRemoved()
        => N("/a\tb/c", HttpsBase).Should().Be("https://example.com/ab/c");

    [Fact]
    public void SpaceInsideHost_IsRejected()
        => N("https://exa mple.com/").Should().BeNull();

    [Fact]
    public void JavascriptSchemeSplitByNewline_IsStillRejected()
    {
        N("java\nscript:alert(1)", HttpsBase).Should().BeNull();
        N("java\tscript:alert(1)").Should().BeNull();
    }

    // ---- length -----------------------------------------------------------------------------------

    [Fact]
    public void VeryLongUrl_5000Chars_IsStillValid()
    {
        var path = new string('a', 5000);
        var input = $"https://example.com/{path}";

        UrlNormalizer.TryNormalize(input, null, out var uri).Should().BeTrue();

        uri.AbsolutePath.Should().Be("/" + path);
        uri.AbsoluteUri.Should().Be(input);
    }

    [Fact]
    public void VeryLongQuery_IsStillValid()
    {
        var input = "https://example.com/s?q=" + new string('x', 10_000);
        N(input).Should().Be(input);
    }

    // ---- non-http schemes -----------------------------------------------------------------------

    [Theory]
    [InlineData("JAVASCRIPT:void(0)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("jAvAsCrIpT:alert(1)")]
    [InlineData("mailto:John Doe <john@example.com>")]
    [InlineData("mailto: someone@example.com")]
    [InlineData("MAILTO:a@b.com?subject=hello world")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("data:text/html;charset=utf-8,%3Ch1%3Ehi%3C%2Fh1%3E")]
    [InlineData("DATA:image/png;base64,iVBORw0KGgo=")]
    [InlineData("tel:+1 (555) 123-4567")]
    [InlineData("sms:+15551234567")]
    [InlineData("about:blank")]
    [InlineData("blob:https://example.com/uuid")]
    [InlineData("chrome-extension://abc/page.html")]
    [InlineData("ws://example.com/socket")]
    public void NonHttpSchemes_AreRejectedWithAndWithoutBase(string input)
    {
        N(input, HttpsBase).Should().BeNull();
        N(input).Should().BeNull();
    }

    // ---- dot segments / colons in relative refs ---------------------------------------------------

    [Theory]
    [InlineData("../../../../x", "https://example.com/x")]
    [InlineData("/../x", "https://example.com/x")]
    [InlineData("/a/./b/../c", "https://example.com/a/c")]
    [InlineData("./", "https://example.com/dir/")]
    [InlineData("..", "https://example.com/")]
    public void DotSegments_AreResolvedAndClampedAtRoot(string input, string expected)
        => N(input, HttpsBase).Should().Be(expected);

    [Fact]
    public void DotSegmentsInAbsoluteUrl_AreResolved()
        => N("https://example.com/a/b/../../../../c").Should().Be("https://example.com/c");

    [Fact]
    public void PathWithColon_IsNotMistakenForScheme()
        => N("/wiki/Special:Search", HttpsBase).Should().Be("https://example.com/wiki/Special:Search");

    [Fact]
    public void RelativeWithColonAfterQuery_IsNotMistakenForScheme()
        => N("?redirect=https://x.org", HttpsBase).Should().Be("https://example.com/dir/page.html?redirect=https://x.org");

    // ---- Normalize() ------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/a b?q=x y", "https://example.com/a%20b?q=x%20y")]
    [InlineData("https://user:pw@bücher.example:443/a/#f", "https://xn--bcher-kva.example/a/")]
    public void Normalize_ReturnsEscapedAbsoluteUri(string input, string expected)
        => UrlNormalizer.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("https://bücher.example/a b/")]
    [InlineData("https://user:pw@example.com/x")]
    [InlineData("https://example.com/a\tb")]
    public void Normalize_Twice_IsIdempotent(string input)
    {
        var once = UrlNormalizer.Normalize(input);
        UrlNormalizer.Normalize(once).Should().Be(once);
    }
}

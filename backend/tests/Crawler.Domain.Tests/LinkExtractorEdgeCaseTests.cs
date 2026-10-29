using System.Diagnostics;
using System.Text;
using Crawler.Domain.Crawling;
using FluentAssertions;

namespace Crawler.Domain.Tests;

/// <summary>Edge cases; where behaviour is a judgement call the test documents the actual behaviour.</summary>
public class LinkExtractorEdgeCaseTests
{
    private static readonly Uri Page = new("https://example.com/dir/page.html");

    private static string[] LinksOf(string html, Uri? page = null)
        => LinkExtractor.Parse(html, page ?? Page).Links.Select(l => l.AbsoluteUri).ToArray();

    // ---- attribute parsing ------------------------------------------------------------------------

    [Theory]
    [InlineData("""<A HREF="/upper">x</A>""")]
    [InlineData("""<a HREF="/upper">x</a>""")]
    [InlineData("""<a Href='/upper'>x</a>""")]
    [InlineData("""<a href=/upper>x</a>""")]
    public void UppercaseTagOrAttribute_IsRecognized(string html)
        => LinksOf(html).Should().Equal("https://example.com/upper");

    [Fact]
    public void HrefWithSurroundingWhitespace_IsTrimmed()
        => LinksOf("""<a href=" /a ">x</a>""").Should().Equal("https://example.com/a");

    [Theory]
    [InlineData("""<a href="">x</a>""")]
    [InlineData("""<a href="   ">x</a>""")]
    [InlineData("""<a href>x</a>""")]
    public void EmptyHref_IsIgnored(string html)
        => LinksOf(html).Should().BeEmpty();

    [Fact]
    public void EntitiesInHref_AreDecoded()
        => LinksOf("""<a href="/p?a=1&amp;b=2&#38;c=3">x</a>""").Should().Equal("https://example.com/p?a=1&b=2&c=3");

    [Fact]
    public void HrefWithEmbeddedNewline_IsCleanedUp()
        => LinksOf("<a href=\"/multi\nline\">x</a>").Should().Equal("https://example.com/multiline");

    // ---- self links -------------------------------------------------------------------------------

    [Theory]
    [InlineData("""<a href="#top">t</a>""")]
    [InlineData("""<a href="#">t</a>""")]
    [InlineData("""<a href="page.html">t</a>""")]
    [InlineData("""<a href="page.html#section">t</a>""")]
    [InlineData("""<a href="https://example.com/dir/page.html">t</a>""")]
    [InlineData("""<a href="HTTPS://EXAMPLE.COM:443/dir/page.html#x">t</a>""")]
    [InlineData("""<a href="./page.html">t</a>""")]
    public void LinkResolvingToPageItself_IsDropped(string html)
        => LinksOf(html).Should().BeEmpty();

    [Theory]
    [InlineData("""<a href="?q=1">t</a>""", "https://example.com/dir/page.html?q=1")]
    [InlineData("""<a href="page.html/">t</a>""", "https://example.com/dir/page.html/")]
    [InlineData("""<a href="http://example.com/dir/page.html">t</a>""", "http://example.com/dir/page.html")]
    public void NearSelfLinks_AreDifferentResourcesAndKept(string html, string expected)
        => LinksOf(html).Should().Equal(expected);

    [Fact]
    public void SelfLink_WhenPageUrlHasFragmentOrUserinfo_IsStillDetected()
        => LinksOf("""<a href="/dir/page.html">x</a><a href="/other">o</a>""", new Uri("https://u:p@Example.com/dir/page.html#frag"))
            .Should().Equal("https://example.com/other");

    [Fact]
    public void SelfLinkWithQuery_IsDroppedWhenPageHasSameQuery()
        => LinksOf("""<a href="?q=1">x</a><a href="?q=2">y</a>""", new Uri("https://example.com/s?q=1"))
            .Should().Equal("https://example.com/s?q=2");

    // ---- hidden / non-rendered content ----------------------------------------------------------

    [Fact]
    public void AnchorInsideNoscript_IsExtracted()
        => LinksOf("""<noscript><a href="/ns">x</a></noscript><a href="/b">b</a>""")
            .Should().Equal("https://example.com/ns", "https://example.com/b");

    [Fact]
    public void AnchorInsideHtmlComment_IsIgnored()
        => LinksOf("""<!-- <a href="/commented">x</a> --><a href="/real">r</a>""")
            .Should().Equal("https://example.com/real");

    [Fact]
    public void AnchorInsideScriptString_IsIgnored()
        => LinksOf("""<script>document.write('<a href="/js">x</a>');</script><a href="/real">r</a>""")
            .Should().Equal("https://example.com/real");

    // ---- <base href> ------------------------------------------------------------------------------

    [Fact]
    public void RelativeBaseHref_IsResolvedAgainstPageThenUsedForLinks()
        => LinksOf("""<html><head><base href="/sub/"></head><body><a href="x">x</a><a href="../y">y</a></body></html>""")
            .Should().Equal("https://example.com/sub/x", "https://example.com/y");

    [Fact]
    public void MultipleBaseTags_FirstWins()
        => LinksOf("""<base href="/one/"><base href="/two/"><a href="x">x</a>""")
            .Should().Equal("https://example.com/one/x");

    [Theory]
    [InlineData("javascript:void(0)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://files.example.com/")]
    [InlineData("mailto:a@b.com")]
    public void BaseHrefWithNonHttpScheme_IsIgnored(string baseHref)
        => LinksOf($"""<base href="{baseHref}"><a href="x">x</a><a href="https://ok.org/">ok</a>""")
            .Should().Equal("https://example.com/dir/x", "https://ok.org/");

    [Fact]
    public void BaseHrefWithEntities_IsDecoded()
        => LinksOf("""<base href="https://cdn.example.com/a?x=1&amp;y=2"><a href="?z=3">z</a>""")
            .Should().Equal("https://cdn.example.com/a?z=3");

    // ---- other elements -----------------------------------------------------------------------------

    [Fact]
    public void NestedAnchors_AllHrefsExtracted()
        => LinksOf("""<a href="/outer">o <a href="/inner">i</a></a>""")
            .Should().BeEquivalentTo("https://example.com/outer", "https://example.com/inner");

    [Fact]
    public void AreaHref_IsIgnored()
        => LinksOf("""<map name="m"><area href="/area" shape="rect"></map><a href="/a">a</a>""")
            .Should().Equal("https://example.com/a");

    [Theory]
    [InlineData("""<link rel="next" href="/next">""")]
    [InlineData("""<img src="/img.png">""")]
    [InlineData("""<iframe src="/frame"></iframe>""")]
    [InlineData("""<form action="/submit"></form>""")]
    public void NonAnchorReferences_AreIgnored(string html)
        => LinksOf(html).Should().BeEmpty();

    [Fact]
    public void MalformedHtml_DoesNotThrow()
    {
        var act = () => LinkExtractor.Parse("""<html><a href="/a"><div><a href='/b'<p>unclosed <a href="/c""", Page);
        act.Should().NotThrow();
    }

    [Fact]
    public void IdnLinks_AreReturnedInPunycodeAndDeduplicatedAcrossSpellings()
        => LinksOf("""<a href="https://bücher.example/">a</a><a href="https://xn--bcher-kva.example/">b</a>""")
            .Should().Equal("https://xn--bcher-kva.example/");

    // ---- title --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<title>A &amp; B</title>", "A & B")]
    [InlineData("<title>&lt;tag&gt; &quot;q&quot;</title>", "<tag> \"q\"")]
    [InlineData("<title>caf&eacute; &#233;</title>", "café é")]
    public void Title_IsEntityDecoded(string html, string expected)
        => LinkExtractor.Parse(html, Page).Title.Should().Be(expected);

    [Fact]
    public void Title_HeadTitlePreferredOverSvgTitleInBody()
    {
        var html = """
            <html><head><title>Real</title></head>
            <body><svg><title>Icon</title></svg></body></html>
            """;
        LinkExtractor.Parse(html, Page).Title.Should().Be("Real");
    }

    [Fact]
    public void Title_HeadTitlePreferredEvenWhenSvgTitleComesFirstInSource()
    {
        // HtmlAgilityPack keeps the source order; a stray svg title before <head> must not win.
        var html = """<svg><title>Icon</title></svg><html><head><title>Real</title></head><body></body></html>""";
        LinkExtractor.Parse(html, Page).Title.Should().Be("Real");
    }

    [Fact]
    public void Title_NoHead_FallsBackToAnyTitle()
        => LinkExtractor.Parse("<body><svg><title>Only</title></svg></body>", Page).Title.Should().Be("Only");

    // ---- performance ------------------------------------------------------------------------------

    [Fact]
    public void Anchors_2000_ParsedQuickly()
    {
        var sb = new StringBuilder("<html><body>");
        for (var i = 0; i < 2000; i++) sb.Append($"<a href=\"/p{i}\">{i}</a>");
        sb.Append("<a href=\"/p0\">dup</a></body></html>");
        var html = sb.ToString();
        LinkExtractor.Parse(html, Page); // warm-up (JIT)

        var sw = Stopwatch.StartNew();
        var links = LinkExtractor.Parse(html, Page).Links;
        sw.Stop();

        links.Should().HaveCount(2000);
        links[0].AbsoluteUri.Should().Be("https://example.com/p0");
        links[^1].AbsoluteUri.Should().Be("https://example.com/p1999");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}

using Crawler.Domain.Crawling;
using FluentAssertions;

namespace Crawler.Domain.Tests;

public class LinkExtractorTests
{
    private static readonly Uri Page = new("https://example.com/dir/page.html");

    private static string[] LinksOf(string html, Uri? page = null)
        => LinkExtractor.Parse(html, page ?? Page).Links.Select(l => l.ToString()).ToArray();

    [Fact]
    public void Parse_Title_IsExtractedAndTrimmed()
        => LinkExtractor.Parse("<html><head><title>  Hello World \n</title></head><body></body></html>", Page)
            .Title.Should().Be("Hello World");

    [Theory]
    [InlineData("<html><body><a href='/x'>x</a></body></html>")]
    [InlineData("<html><head><title></title></head></html>")]
    [InlineData("<html><head><title>   </title></head></html>")]
    [InlineData("")]
    public void Parse_MissingOrBlankTitle_IsNull(string html)
        => LinkExtractor.Parse(html, Page).Title.Should().BeNull();

    [Fact]
    public void Parse_RelativeAndAbsoluteLinks_ResolvedAgainstPage()
    {
        var html = """
            <a href="about.html">a</a>
            <a href="/root">b</a>
            <a href="../up">c</a>
            <a href="https://other.org/x">d</a>
            <a href="?q=1">e</a>
            """;
        LinksOf(html).Should().Equal(
            "https://example.com/dir/about.html",
            "https://example.com/root",
            "https://example.com/up",
            "https://other.org/x",
            "https://example.com/dir/page.html?q=1");
    }

    [Fact]
    public void Parse_BaseHref_IsHonoured()
    {
        var html = """
            <html><head><base href="https://cdn.example.org/base/"></head>
            <body><a href="rel.html">r</a><a href="/abs">a</a></body></html>
            """;
        LinksOf(html).Should().Equal("https://cdn.example.org/base/rel.html", "https://cdn.example.org/abs");
    }

    [Fact]
    public void Parse_RelativeBaseHref_ResolvedAgainstPage()
    {
        var html = """<head><base href="/other/"></head><a href="x">x</a>""";
        LinksOf(html).Should().Equal("https://example.com/other/x");
    }

    [Fact]
    public void Parse_DuplicateLinks_CollapsedButTrailingSlashVariantIsDistinct()
    {
        var html = """
            <a href="/a">1</a><a href="/a">2</a>
            <a href="https://example.com/a">3</a>
            <a href="/a/">4</a>
            """;
        LinksOf(html).Should().Equal("https://example.com/a", "https://example.com/a/");
    }

    [Fact]
    public void Parse_SameUrlDifferentFragments_CollapsedToOne()
    {
        var html = """<a href="/a#one">1</a><a href="/a#two">2</a><a href="/a">3</a>""";
        LinksOf(html).Should().Equal("https://example.com/a");
    }

    [Theory]
    [InlineData("""<a href="mailto:a@b.com">m</a>""")]
    [InlineData("""<a href="tel:+1234">t</a>""")]
    [InlineData("""<a href="javascript:void(0)">j</a>""")]
    [InlineData("""<a href="data:text/plain,hi">d</a>""")]
    [InlineData("""<a href="ftp://example.com/f">f</a>""")]
    public void Parse_NonHttpSchemes_AreIgnored(string html)
        => LinksOf(html).Should().BeEmpty();

    [Fact]
    public void Parse_NonHttpMixedWithHttp_KeepsOnlyHttp()
    {
        var html = """<a href="mailto:a@b.com">m</a><a href="/ok">ok</a><a href="tel:1">t</a>""";
        LinksOf(html).Should().Equal("https://example.com/ok");
    }

    [Fact]
    public void Parse_AmpersandEntityInHref_IsDecoded()
    {
        var html = """<a href="/search?a=1&amp;b=2">s</a>""";
        LinksOf(html).Should().Equal("https://example.com/search?a=1&b=2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body><p>No links here</p></body></html>")]
    public void Parse_NoAnchors_ReturnsEmptyList(string html)
        => LinkExtractor.Parse(html, Page).Links.Should().BeEmpty();

    [Fact]
    public void Parse_Order_IsPreservedByFirstOccurrence()
    {
        var html = """
            <a href="/c">c</a><a href="/a">a</a><a href="/b">b</a><a href="/a">a2</a><a href="/c">c2</a>
            """;
        LinksOf(html).Should().Equal(
            "https://example.com/c", "https://example.com/a", "https://example.com/b");
    }

    [Fact]
    public void Parse_AnchorsWithoutHref_AreIgnored()
    {
        var html = """<a name="top">anchor</a><a>plain</a><a href="/real">r</a>""";
        LinksOf(html).Should().Equal("https://example.com/real");
    }

    [Fact]
    public void Parse_FragmentOnlyHref_IsSelfLinkAndDropped()
    {
        var html = """<a href="#top">t</a>""";
        LinksOf(html).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ReturnsTitleAndLinksTogether()
    {
        var result = LinkExtractor.Parse("<title>T</title><a href='/x'>x</a>", Page);
        result.Title.Should().Be("T");
        result.Links.Should().ContainSingle().Which.ToString().Should().Be("https://example.com/x");
    }
}

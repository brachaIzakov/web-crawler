using HtmlAgilityPack;

namespace Crawler.Domain.Crawling;

public sealed record ParsedPage(string? Title, IReadOnlyList<Uri> Links);

/// <summary>
/// Extracts &lt;a href&gt; links from an HTML document, resolves them against the page URL
/// (honouring an http(s) &lt;base href&gt;), normalizes them and removes duplicates while preserving order.
/// Links that resolve to the page itself (e.g. <c>href="#top"</c>) are not outgoing links and are dropped.
/// </summary>
public static class LinkExtractor
{
    public static ParsedPage Parse(string html, Uri pageUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var baseUri = ResolveBaseUri(doc, pageUri);
        var title = ExtractTitle(doc);
        var self = UrlNormalizer.TryNormalize(pageUri.AbsoluteUri, null, out var selfUri) ? selfUri.AbsoluteUri : null;

        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return new ParsedPage(title, Array.Empty<Uri>());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<Uri>();
        foreach (var anchor in anchors)
        {
            var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", string.Empty));
            if (!UrlNormalizer.TryNormalize(href, baseUri, out var uri)) continue;

            // AbsoluteUri is the escaped form used as the page identity everywhere else (DB unique key, engine caches).
            var key = uri.AbsoluteUri;
            if (key == self) continue;
            if (seen.Add(key)) links.Add(uri);
        }

        return new ParsedPage(title, links);
    }

    private static string? ExtractTitle(HtmlDocument doc)
    {
        // Prefer the document title; an SVG <title> deeper in the body is not the page title.
        var node = doc.DocumentNode.SelectSingleNode("//head/title") ?? doc.DocumentNode.SelectSingleNode("//title");
        var text = node is null ? null : HtmlEntity.DeEntitize(node.InnerText).Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static Uri ResolveBaseUri(HtmlDocument doc, Uri pageUri)
    {
        var baseHref = doc.DocumentNode.SelectSingleNode("//base[@href]")?.GetAttributeValue("href", null);
        if (!string.IsNullOrWhiteSpace(baseHref)
            && Uri.TryCreate(pageUri, HtmlEntity.DeEntitize(baseHref), out var resolved)
            && (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
            return resolved;
        return pageUri;
    }
}

using System.Collections.Concurrent;
using Crawler.Worker.Crawling;

namespace Crawler.Worker.Tests;

/// <summary>
/// Serves the HTML files under ./Fixtures for http://site.test/* and simulates
/// a non-HTML asset, a 404 and an unreachable external host. No network involved.
/// Optional <c>redirects</c> (absolute URL → final URL) simulate followed redirects: the fixture of the
/// final URL is served with <see cref="FetchResult.FinalUrl"/> set, and the target hosts are served too.
/// </summary>
public sealed class FixturePageFetcher : IPageFetcher
{
    public const string Host = "site.test";

    private static readonly Dictionary<string, string> PathToFixture = new(StringComparer.Ordinal)
    {
        ["/"] = "index.html",
        ["/about"] = "about.html",
        ["/products"] = "products.html",
        ["/products/widget"] = "widget.html",
        ["/products/widget/specs"] = "specs.html",
        ["/deep"] = "deep.html",
        ["/deeper"] = "deeper.html",
        ["/docs/"] = "docs.html",
        ["/docs/intro"] = "intro.html",
        ["/start"] = "start.html",
        ["/start-both"] = "start-both.html",
        ["/guide/"] = "guide.html",
        ["/guide/next"] = "guide-next.html",
    };

    private readonly string _fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly Func<Uri, Task>? _onFetch;
    private readonly IReadOnlyDictionary<string, string> _redirects;
    private readonly HashSet<string> _servedHosts = new(StringComparer.OrdinalIgnoreCase) { Host };

    public FixturePageFetcher(Func<Uri, Task>? onFetch = null, IReadOnlyDictionary<string, string>? redirects = null)
    {
        _onFetch = onFetch;
        _redirects = redirects ?? new Dictionary<string, string>();
        foreach (var target in _redirects.Values) _servedHosts.Add(new Uri(target).Host);
    }

    /// <summary>Thread-safe: the engine may fetch pages of one BFS level concurrently.</summary>
    public ConcurrentQueue<Uri> Fetched { get; } = new();

    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        Fetched.Enqueue(url);
        if (_onFetch is not null) await _onFetch(url);

        Uri? finalUrl = _redirects.TryGetValue(url.AbsoluteUri, out var target) ? new Uri(target) : null;
        url = finalUrl ?? url;

        if (!_servedHosts.Contains(url.Host))
            return FetchResult.Failed(null, "Host unreachable");

        if (url.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return FetchResult.NotHtml(200, "image/png");

        if (!PathToFixture.TryGetValue(url.AbsolutePath, out var file))
            return FetchResult.Failed(404, "Not Found");

        var html = await File.ReadAllTextAsync(Path.Combine(_fixtureDir, file), cancellationToken);
        return FetchResult.Ok(200, html, finalUrl);
    }
}

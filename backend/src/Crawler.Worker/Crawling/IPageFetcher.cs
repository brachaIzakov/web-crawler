namespace Crawler.Worker.Crawling;

/// <param name="FinalUrl">URL of the response after automatic redirects (raw, not normalized); null when unknown.</param>
public sealed record FetchResult(int? StatusCode, bool IsHtml, string? Html, string? Error, Uri? FinalUrl = null)
{
    public bool Succeeded => Html is not null && IsHtml && Error is null;

    /// <summary>The response was fine but not an HTML document (the page is Skipped, not Failed).</summary>
    public bool IsNotHtml { get; init; }

    public static FetchResult Ok(int statusCode, string html, Uri? finalUrl = null) => new(statusCode, true, html, null, finalUrl);

    public static FetchResult NotHtml(int statusCode, string contentType)
        => new(statusCode, false, null, $"Not HTML: {contentType}") { IsNotHtml = true };

    public static FetchResult Failed(int? statusCode, string error) => new(statusCode, false, null, error);
}

public interface IPageFetcher
{
    /// <summary>
    /// Fetches a page. Never throws for HTTP/network failures (returns <see cref="FetchResult.Failed"/>);
    /// only throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken);
}

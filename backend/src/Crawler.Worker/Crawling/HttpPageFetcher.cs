using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Crawler.Worker.Crawling;

/// <summary>
/// Fetches pages through a typed <see cref="HttpClient"/> whose resilience pipeline (timeouts, retries with
/// exponential backoff + jitter, per-authority circuit breaker) and SSRF guard are configured in Program.cs.
/// Redirects are followed by the handler; callers keep the requested URL as the page identity and receive the
/// raw post-redirect URL as <see cref="FetchResult.FinalUrl"/> (links must be resolved against it).
/// Errors are reported as fixed, sanitized strings; exception details are only logged.
/// </summary>
public sealed partial class HttpPageFetcher(HttpClient httpClient, ILogger<HttpPageFetcher> logger) : IPageFetcher
{
    /// <summary>Bodies are truncated (not rejected) at this size, even when Content-Length announces more.</summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    private const int ChunkBytes = 16 * 1024;
    private const int CharsetPrescanBytes = 2048;
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] HtmlMediaTypes = ["text/html", "application/xhtml+xml"];

    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
                return FetchResult.Failed(statusCode, DescribeStatus(response));

            var contentType = response.Content.Headers.ContentType;
            if (!IsHtml(contentType))
                return FetchResult.NotHtml(statusCode, contentType?.MediaType ?? "unknown");

            var html = await ReadBodyAsync(response.Content, cancellationToken);
            return FetchResult.Ok(statusCode, html, response.RequestMessage?.RequestUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var error = DescribeException(ex);
            if (error == UnexpectedError) logger.LogWarning(ex, "Fetching {Url} failed unexpectedly", url);
            else logger.LogDebug(ex, "Fetching {Url} failed: {Error}", url, error);
            return FetchResult.Failed(null, error);
        }
    }

    /// <summary>Primary handler of the fetcher's HttpClient (the resilience pipeline is layered on top in Program.cs).</summary>
    public static SocketsHttpHandler CreateHandler(CrawlOptions options) => new()
    {
        // MaxRedirects = 0 means "do not follow"; the handler itself rejects MaxAutomaticRedirections = 0.
        AllowAutoRedirect = options.Http.MaxRedirects > 0,
        MaxAutomaticRedirections = Math.Max(1, options.Http.MaxRedirects),
        MaxConnectionsPerServer = options.Http.MaxConnectionsPerServer,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        // A proxy would resolve and dial the target itself, bypassing the SSRF guard.
        UseProxy = options.AllowPrivateAddresses,
        ConnectCallback = options.AllowPrivateAddresses ? null : SafeAddressPolicy.ConnectAsync
    };

    private static bool IsHtml(MediaTypeHeaderValue? contentType)
        => contentType?.MediaType is { } mediaType
           && HtmlMediaTypes.Any(html => mediaType.Contains(html, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- sanitized errors

    private const string UnexpectedError = "Unexpected error";

    // The handler stops after MaxAutomaticRedirections and returns the last 3xx response as-is.
    private static string DescribeStatus(HttpResponseMessage response)
        => (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null
            ? "Too many redirects"
            : $"HTTP {(int)response.StatusCode}";

    internal static string DescribeException(Exception exception)
    {
        var chain = Flatten(exception).ToList();
        if (chain.Any(e => e is BlockedAddressException)) return "Blocked address";
        if (chain.Any(e => e is AuthenticationException)) return "TLS handshake failed";
        if (chain.Any(e => e is TimeoutRejectedException or TimeoutException or OperationCanceledException)) return "Timed out";
        if (chain.Any(e => e is HttpRequestException or SocketException or IOException or BrokenCircuitException)) return "Connection failed";
        return UnexpectedError;
    }

    private static IEnumerable<Exception> Flatten(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            yield return current;
    }

    // ---------------------------------------------------------------- body

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // Bounds slow-drip servers: the resilience pipeline's timeouts only cover the response headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BodyReadTimeout);

        await using var stream = await content.ReadAsStreamAsync(timeout.Token);
        using var body = new MemoryStream((int)Math.Clamp(content.Headers.ContentLength ?? ChunkBytes, 0, MaxBodyBytes));
        var chunk = new byte[ChunkBytes];
        while (body.Length < MaxBodyBytes)
        {
            var wanted = (int)Math.Min(ChunkBytes, MaxBodyBytes - body.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), timeout.Token);
            if (read == 0) break;
            body.Write(chunk, 0, read);
        }

        // Spans cannot live across awaits in C# 12, so decoding happens in a synchronous helper.
        return DecodeBuffer(body.GetBuffer(), (int)body.Length, content.Headers.ContentType?.CharSet);
    }

    private static string DecodeBuffer(byte[] buffer, int length, string? headerCharset)
        => Decode(new ReadOnlySpan<byte>(buffer, 0, length), headerCharset);

    // ---------------------------------------------------------------- charset

    /// <summary>Encoding precedence: byte order mark → Content-Type charset → &lt;meta&gt; prescan → UTF-8.</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes, string? headerCharset)
    {
        var (bomEncoding, bomLength) = DetectBom(bytes);
        var encoding = bomEncoding ?? GetEncoding(headerCharset) ?? GetMetaEncoding(bytes) ?? Encoding.UTF8;
        return encoding.GetString(bytes[bomLength..]);
    }

    private static (Encoding? Encoding, int Length) DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (Encoding.UTF8, 3);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return (Encoding.Unicode, 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return (Encoding.BigEndianUnicode, 2);
        return (null, 0);
    }

    private static Encoding? GetMetaEncoding(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, CharsetPrescanBytes)]);
        var match = MetaCharsetRegex().Match(head);
        if (!match.Success) return null;

        // Bytes that were readable as ASCII to find this tag cannot be UTF-16/32 (HTML spec: treat as UTF-8).
        var encoding = GetEncoding(match.Groups["charset"].Value);
        return encoding is UnicodeEncoding or UTF32Encoding ? Encoding.UTF8 : encoding;
    }

    private static Encoding? GetEncoding(string? charset)
    {
        var name = charset?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Matches both <meta charset="x"> and <meta http-equiv="Content-Type" content="text/html; charset=x">.
    [GeneratedRegex("""<meta\s[^>]*?charset\s*=\s*["']?\s*(?<charset>[A-Za-z0-9._:\-]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharsetRegex();
}

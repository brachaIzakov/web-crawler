using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Crawler.Worker.Crawling;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests;

public class HttpPageFetcherTests
{
    private static readonly Uri Url = new("https://example.com/page");

    private static HttpPageFetcher Fetcher(StubHandler handler)
        => new(new HttpClient(handler), NullLogger<HttpPageFetcher>.Instance);

    [Fact]
    public void CreateHandler_ZeroMaxRedirects_DisablesRedirectsInsteadOfThrowing()
    {
        var options = new CrawlOptions { Http = { MaxRedirects = 0 } };

        using var handler = HttpPageFetcher.CreateHandler(options);

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string? mediaType, string? charset = null)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = mediaType is null ? null : new MediaTypeHeaderValue(mediaType) { CharSet = charset };
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task Fetch_HtmlUtf8_ReturnsOkWithBody()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "<html>שלום ü</html>", "text/html", "utf-8"));

        var result = await Fetcher(handler).FetchAsync(Url, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.IsHtml.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Html.Should().Be("<html>שלום ü</html>");
        result.Error.Should().BeNull();
        handler.Requests.Should().ContainSingle().Which.RequestUri.Should().Be(Url);
    }

    [Theory]
    [InlineData("application/xhtml+xml")]
    [InlineData("TEXT/HTML")]
    public async Task Fetch_OtherHtmlMediaTypes_AreOk(string mediaType)
    {
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, "<p/>", mediaType))).FetchAsync(Url, default);

        result.Succeeded.Should().BeTrue();
        result.Html.Should().Be("<p/>");
    }

    [Fact]
    public async Task Fetch_Latin1Charset_DecodedWithDeclaredEncoding()
    {
        var bytes = Encoding.Latin1.GetBytes("<p>café</p>");
        var handler = new StubHandler(_ =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "iso-8859-1" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await Fetcher(handler).FetchAsync(Url, default);

        result.Html.Should().Be("<p>café</p>");
    }

    [Fact]
    public async Task Fetch_UnknownCharset_FallsBackToUtf8()
    {
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, "<p>ü</p>", "text/html", "x-bogus")))
            .FetchAsync(Url, default);

        result.Succeeded.Should().BeTrue();
        result.Html.Should().Be("<p>ü</p>");
    }

    [Fact]
    public async Task Fetch_Json_ReturnsNotHtmlWithoutReadingBody()
    {
        var content = new TrackingContent(Encoding.UTF8.GetBytes("{\"a\":1}"), "application/json");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await Fetcher(handler).FetchAsync(Url, default);

        result.Succeeded.Should().BeFalse();
        result.IsHtml.Should().BeFalse();
        result.Html.Should().BeNull();
        result.StatusCode.Should().Be(200);
        result.Error.Should().Be("Not HTML: application/json");
        content.WasRead.Should().BeFalse("non-HTML bodies must not be downloaded");
    }

    [Fact]
    public async Task Fetch_MissingContentType_IsNotHtml()
    {
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, "x", null))).FetchAsync(Url, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Not HTML: unknown");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    public async Task Fetch_ErrorStatus_ReturnsFailedWithStatusCode(HttpStatusCode status, int expected)
    {
        var result = await Fetcher(new StubHandler(_ => Response(status, "<html>error</html>", "text/html")))
            .FetchAsync(Url, default);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(expected);
        result.Html.Should().BeNull();
        result.Error.Should().Be($"HTTP {expected}");
    }

    [Fact]
    public async Task Fetch_HandlerThrowsHttpRequestException_ReturnsFailedWithoutThrowing()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("No such host is known."));

        var result = await Fetcher(handler).FetchAsync(Url, default);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.Error.Should().Be("Connection failed", "errors are sanitized; exception messages never reach the API");
    }

    public static TheoryData<Exception, string> SanitizedErrors => new()
    {
        { new HttpRequestException("secret internal host 10.0.0.5"), "Connection failed" },
        { new HttpRequestException("wrapped", new System.Net.Sockets.SocketException()), "Connection failed" },
        { new IOException("reset"), "Connection failed" },
        { new TimeoutException(), "Timed out" },
        { new HttpRequestException("ssl", new System.Security.Authentication.AuthenticationException()), "TLS handshake failed" },
        { new InvalidOperationException("bug with details"), "Unexpected error" },
    };

    [Theory]
    [MemberData(nameof(SanitizedErrors))]
    public async Task Fetch_HandlerThrows_ErrorIsSanitizedCategory(Exception thrown, string expected)
    {
        var result = await Fetcher(new StubHandler(_ => throw thrown)).FetchAsync(Url, default);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.Error.Should().Be(expected);
    }

    [Fact]
    public async Task Fetch_RedirectNotFollowed_ReportsTooManyRedirects()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/next");
            return response;
        });

        var result = await Fetcher(handler).FetchAsync(Url, default);

        result.StatusCode.Should().Be(302);
        result.Error.Should().Be("Too many redirects");
    }

    [Fact]
    public async Task Fetch_TimeoutNotCausedByCaller_ReturnsFailed()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        var result = await Fetcher(handler).FetchAsync(Url, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Timed out");
    }

    [Fact]
    public async Task Fetch_CallerCancels_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "<p/>", "text/html"));

        var act = () => Fetcher(handler).FetchAsync(Url, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Fetch_BodyLargerThanCap_IsTruncatedToCap()
    {
        var body = new string('a', HttpPageFetcher.MaxBodyBytes + 12_345);
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, body, "text/html", "utf-8")))
            .FetchAsync(Url, default);

        result.Succeeded.Should().BeTrue();
        result.Html!.Length.Should().Be(HttpPageFetcher.MaxBodyBytes);
    }

    [Fact]
    public async Task Fetch_BodyExactlyAtCap_IsReturnedWhole()
    {
        var body = new string('b', HttpPageFetcher.MaxBodyBytes);
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, body, "text/html")))
            .FetchAsync(Url, default);

        result.Html!.Length.Should().Be(HttpPageFetcher.MaxBodyBytes);
    }

    [Fact]
    public async Task Fetch_Redirected_FinalUrlIsResponseRequestUri()
    {
        var redirected = new Uri("https://www.example.com/landing");
        var handler = new StubHandler(_ =>
        {
            var response = Response(HttpStatusCode.OK, "<p/>", "text/html");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, redirected);
            return response;
        });

        var result = await Fetcher(handler).FetchAsync(Url, default);

        result.FinalUrl.Should().Be(redirected);
    }

    [Fact]
    public async Task Fetch_NotRedirected_FinalUrlIsRequestedUrl()
    {
        // The fetcher reports the response's request URI even when nothing was redirected;
        // the engine compares it against the page URL and only keeps it when it differs.
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, "<p/>", "text/html")))
            .FetchAsync(Url, default);

        result.FinalUrl.Should().Be(Url);
    }

    [Fact]
    public async Task Fetch_Failed_HasNoFinalUrl()
    {
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.NotFound, "", "text/html")))
            .FetchAsync(Url, default);

        result.FinalUrl.Should().BeNull();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>Content that records whether anyone asked for its body.</summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _bytes;

        public TrackingContent(byte[] bytes, string mediaType)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            WasRead = true;
            return Task.FromResult<Stream>(new MemoryStream(_bytes));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }
}

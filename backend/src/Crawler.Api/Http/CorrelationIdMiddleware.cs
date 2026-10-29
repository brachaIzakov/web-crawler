using Serilog.Context;

namespace Crawler.Api.Http;

/// <summary>
/// Reads <c>X-Correlation-Id</c> (or creates one), exposes it as <see cref="HttpContext.TraceIdentifier"/>,
/// pushes it to the log context and echoes it on the response.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context.Request);
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    // Client-supplied ids are only trusted if short and printable, to keep logs and headers clean.
    private static string ResolveCorrelationId(HttpRequest request)
    {
        var incoming = request.Headers[HeaderName].ToString();
        var isUsable = incoming.Length is > 0 and <= MaxLength && incoming.All(c => c is > ' ' and < (char)127);
        return isUsable ? incoming : Guid.NewGuid().ToString("N");
    }
}

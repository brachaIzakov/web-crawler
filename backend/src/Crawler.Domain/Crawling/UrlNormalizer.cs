namespace Crawler.Domain.Crawling;

/// <summary>
/// Resolves and canonicalizes URLs so that the same page is never processed twice within a job.
/// Rules: resolve relative to base, http(s) only, drop fragment, lowercase scheme, punycode + lowercase host,
/// strip default ports, strip userinfo, drop empty query, strip embedded whitespace.
/// Trailing slashes are kept: <c>/docs</c> and <c>/docs/</c> are different resources (relative links resolve
/// differently against them); servers that treat them as aliases answer with a redirect, which the crawler
/// records as the page's final URL.
/// </summary>
public static class UrlNormalizer
{
    private static readonly char[] StrippedWhitespace = ['\t', '\r', '\n'];

    public static bool TryNormalize(string? candidate, Uri? baseUri, out Uri normalized)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        // Browsers strip tab/CR/LF anywhere in a URL before parsing.
        var trimmed = candidate.Trim();
        if (trimmed.IndexOfAny(StrippedWhitespace) >= 0)
            trimmed = string.Concat(trimmed.Split(StrippedWhitespace, StringSplitOptions.None));

        // Scheme-only or non-navigational links (mailto:, tel:, javascript:, data:) are ignored.
        if (HasNonHttpScheme(trimmed)) return false;

        Uri? resolved;
        if (baseUri is null)
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out resolved)) return false;
        }
        else if (!Uri.TryCreate(baseUri, trimmed, out resolved))
        {
            return false;
        }

        if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(resolved.Host)) return false;

        var builder = new UriBuilder(resolved)
        {
            Scheme = resolved.Scheme.ToLowerInvariant(),
            // IdnHost is the punycode form, so "bücher.example" and "xn--bcher-kva.example" get one identity.
            Host = resolved.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        if (resolved.IsDefaultPort) builder.Port = -1;
        if (string.IsNullOrEmpty(builder.Path)) builder.Path = "/";
        if (builder.Query == "?") builder.Query = string.Empty;

        normalized = UppercasePercentEncoding(builder.Uri);
        return true;
    }

    /// <summary>
    /// RFC 3986 §6.2.2.1: <c>%2f</c> and <c>%2F</c> are the same octet. System.Uri already upper-cases the escapes it
    /// produces but keeps reserved ones (such as <c>%2f</c>) as written, which would give one page two identities.
    /// </summary>
    private static Uri UppercasePercentEncoding(Uri uri)
    {
        var text = uri.AbsoluteUri;
        var chars = text.ToCharArray();
        var changed = false;
        for (var i = text.IndexOf('%'); i >= 0 && i + 2 < chars.Length; i = text.IndexOf('%', i + 1))
        {
            for (var j = i + 1; j <= i + 2; j++)
            {
                if (chars[j] is >= 'a' and <= 'f')
                {
                    chars[j] = char.ToUpperInvariant(chars[j]);
                    changed = true;
                }
            }
        }
        return changed ? new Uri(new string(chars)) : uri;
    }

    public static string Normalize(string absoluteUrl)
    {
        if (!TryNormalize(absoluteUrl, null, out var uri))
            throw new ArgumentException($"'{absoluteUrl}' is not a valid absolute http(s) URL.", nameof(absoluteUrl));
        return uri.AbsoluteUri;
    }

    /// <summary>Hosts compare in their punycode, lower-case form so IDN spellings never split a domain.</summary>
    public static string CanonicalHost(Uri uri) => uri.IdnHost.ToLowerInvariant();

    private static bool HasNonHttpScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        // "//host/path" and "/path:with:colons" are not schemes.
        var slash = value.IndexOf('/');
        if (slash >= 0 && slash < colon) return false;

        var scheme = value[..colon];
        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
        }
        return !scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    }
}

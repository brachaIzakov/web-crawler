using System.Text;

namespace Crawler.Worker.Crawling;

/// <summary>
/// Makes page-supplied text storable in a Postgres <c>text</c> column. Postgres rejects NUL characters and Npgsql
/// rejects lone surrogates (not valid UTF-8); either would fail the whole save and dead-letter the job.
/// </summary>
public static class TextLimits
{
    /// <summary>Drops NUL characters and replaces unpaired surrogates with U+FFFD.</summary>
    public static string? Sanitize(string? value)
    {
        if (value is null || !NeedsSanitizing(value)) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0') continue;
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
                continue;
            }
            builder.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }
        return builder.ToString();
    }

    /// <summary>Cuts to at most <paramref name="maxLength"/> UTF-16 code units without splitting a surrogate pair.</summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength) return value;
        var length = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length];
    }

    private static bool NeedsSanitizing(string value)
    {
        foreach (var c in value)
        {
            if (c == '\0' || char.IsSurrogate(c)) return true;
        }
        return false;
    }
}

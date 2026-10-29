namespace Crawler.Domain.Crawling;

public static class DomainLinkRatio
{
    /// <summary>
    /// (# outgoing links whose host equals the starting domain) / (total # outgoing links).
    /// Returns 0 when there are no outgoing links. Hosts compare case-insensitively in punycode form;
    /// ports and schemes are ignored (the "domain" is the host only).
    /// </summary>
    public static double Compute(IReadOnlyCollection<Uri> outgoingLinks, string startingDomain)
        => Compute(outgoingLinks.Count, CountInternal(outgoingLinks, startingDomain));

    public static double Compute(int outgoingLinkCount, int internalLinkCount)
        => outgoingLinkCount == 0 ? 0d : (double)internalLinkCount / outgoingLinkCount;

    public static int CountInternal(IEnumerable<Uri> outgoingLinks, string startingDomain)
        => CountInternalTo(outgoingLinks, Canonicalize(startingDomain));

    public static bool IsInternal(Uri link, string startingDomain)
        => IsInternalTo(link, Canonicalize(startingDomain));

    /// <summary>
    /// Fast path for many links against one domain: <paramref name="canonicalHost"/> must come from
    /// <see cref="Canonicalize"/>, computed once by the caller.
    /// </summary>
    public static int CountInternalTo(IEnumerable<Uri> outgoingLinks, string canonicalHost)
    {
        ArgumentNullException.ThrowIfNull(outgoingLinks);
        var count = 0;
        foreach (var link in outgoingLinks)
            if (IsInternalTo(link, canonicalHost)) count++;
        return count;
    }

    /// <summary>Fast path: <paramref name="canonicalHost"/> must come from <see cref="Canonicalize"/>.</summary>
    public static bool IsInternalTo(Uri link, string canonicalHost)
        // IdnHost is ASCII (punycode), so an ordinal case-insensitive compare equals comparing lower-cased forms.
        => string.Equals(link.IdnHost, canonicalHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical (lower-case punycode) form of a starting domain; accepts either spelling of an IDN.</summary>
    public static string Canonicalize(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try { return new System.Globalization.IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return host.ToLowerInvariant(); }
    }
}

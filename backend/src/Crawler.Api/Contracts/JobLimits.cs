namespace Crawler.Api.Contracts;

public static class JobLimits
{
    public const int MinDepth = 1;
    public const int MaxDepth = 5;
    public const int DefaultDepth = 2;

    public const int MinPages = 1;
    public const int MaxPages = 1000;
    public const int DefaultPages = 200;

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    /// <summary>Keeps the OFFSET arithmetic in int range and rejects absurd pages early.</summary>
    public const int MaxPage = 1_000_000;

    /// <summary>Matches the column length of jobs.InputUrl / pages.Url.</summary>
    public const int MaxUrlLength = 2048;
}

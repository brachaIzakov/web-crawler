using Crawler.Domain.Entities;

namespace Crawler.Api.Services;

public static class JobProgress
{
    /// <summary>
    /// 0 while pending, 100 once completed; otherwise crawled / discovered (discovered capped at maxPages,
    /// since the crawler never fetches more than that). A running job is held at 99 until it actually
    /// completes; Failed/Canceled report how far they got.
    /// </summary>
    public static int Percent(JobStatus status, int pagesCrawled, int pagesDiscovered, int maxPages) => status switch
    {
        JobStatus.Pending => 0,
        JobStatus.Completed => 100,
        JobStatus.Running => Math.Min(99, Ratio(pagesCrawled, pagesDiscovered, maxPages)),
        _ => Math.Min(100, Ratio(pagesCrawled, pagesDiscovered, maxPages))
    };

    private static int Ratio(int pagesCrawled, int pagesDiscovered, int maxPages)
        => pagesCrawled * 100 / Math.Max(Math.Min(pagesDiscovered, maxPages), 1);

    /// <summary>Elapsed time from start to completion, or to <paramref name="now"/> while the job is still open.</summary>
    public static long? DurationMs(DateTime? startedAt, DateTime? completedAt, DateTime now)
    {
        if (startedAt is null) return null;
        var end = completedAt ?? now;
        return (long)(end - startedAt.Value).TotalMilliseconds;
    }
}

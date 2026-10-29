using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Crawler.Worker.Tests;

/// <summary>
/// Fetch/parse overlap: documents are parsed inside the per-page fetch task (thread pool, bounded by the fetch
/// window) and handed to the single persistence path, which must stay correct when results arrive out of order.
/// </summary>
public sealed class CrawlEngineConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public CrawlEngineConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = new CrawlerDbContext(DbOptions());
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task RunAsync_ResultsCompleteOutOfFetchOrder_PersistsSameGraph()
    {
        var job = await SeedJobAsync("http://site.test/", maxDepth: 2, maxPages: 200);
        // /about is fetched first at depth 1 but completes last, so its parsed result reaches persistence after its siblings.
        var fetcher = new FixturePageFetcher(onFetch: url => url.AbsolutePath == "/about" ? Task.Delay(200) : Task.CompletedTask);
        var saved = new SavedPagesRecorder();

        await RunEngineAsync(job, fetcher, maxConcurrentFetches: 4, saved);

        var depthOneFetchOrder = fetcher.Fetched.Select(u => u.AbsoluteUri).Where(IsDepthOne).ToList();
        var depthOnePersistOrder = saved.Urls.Where(IsDepthOne).ToList();
        depthOneFetchOrder.First().Should().Be("http://site.test/about");
        depthOnePersistOrder.Last().Should().Be("http://site.test/about", "results are persisted in completion order");
        depthOnePersistOrder.Should().BeEquivalentTo(depthOneFetchOrder);

        using var db = new CrawlerDbContext(DbOptions());
        var savedJob = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        savedJob.Status.Should().Be(JobStatus.Completed);
        savedJob.PagesCrawled.Should().Be(7);
        var pages = await db.Pages.Where(p => p.JobId == job.Id).ToListAsync();
        pages.Should().HaveCount(7);
        (await db.Edges.CountAsync(e => e.JobId == job.Id)).Should().Be(9);
        var about = pages.Single(p => p.Url == "http://site.test/about");
        about.Status.Should().Be(PageStatus.Crawled);
        about.DomainLinkRatio.Should().BeApproximately(0.6, 1e-9);
        pages.Single(p => p.Url == "http://site.test/deep").ParentPageId.Should().Be(about.Id, "first parent in BFS order wins");

        static bool IsDepthOne(string url) => url is "http://site.test/about" or "http://site.test/products"
            or "http://site.test/image.png" or "http://site.test/missing";
    }

    /// <summary>
    /// Timing harness (not a pass/fail benchmark): 30 heavy pages behind a 50 ms fetch delay with a window of 4.
    /// Reports wall-clock time against serial/parallel parse references; asserts only correctness. Run with
    /// DOTNET_gcServer=1 to match the worker (Sdk.Web defaults to Server GC; under the test host's workstation GC the
    /// allocation-heavy HtmlAgilityPack parse does not scale across cores).
    /// </summary>
    [Fact]
    public async Task Harness_ThirtyHeavyPages_With50msFetchDelay_ReportsTiming()
    {
        const int pageCount = 30;
        var job = await SeedJobAsync(SyntheticSiteFetcher.Root, maxDepth: 1, maxPages: pageCount + 1);
        var fetcher = new SyntheticSiteFetcher(pageCount, TimeSpan.FromMilliseconds(50));

        // Warm-up (JIT, HtmlAgilityPack, EF model) on a separate job so the measured run is steady-state.
        var warmup = await SeedJobAsync(SyntheticSiteFetcher.Root, maxDepth: 1, maxPages: 3);
        await RunEngineAsync(warmup, fetcher, maxConcurrentFetches: 4);

        var stopwatch = Stopwatch.StartNew();
        await RunEngineAsync(job, fetcher, maxConcurrentFetches: 4);
        stopwatch.Stop();

        // Reference: the same 30 parses back to back on one thread.
        var parseOnly = Stopwatch.StartNew();
        for (var i = 0; i < pageCount; i++) Crawler.Domain.Crawling.LinkExtractor.Parse(fetcher.PageHtml, new Uri(SyntheticSiteFetcher.Root));
        parseOnly.Stop();
        var parseParallel = Stopwatch.StartNew();
        Parallel.For(0, pageCount, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            _ => Crawler.Domain.Crawling.LinkExtractor.Parse(fetcher.PageHtml, new Uri(SyntheticSiteFetcher.Root)));
        parseParallel.Stop();

        _output.WriteLine(
            $"pages={pageCount + 1} html={fetcher.PageHtml.Length / 1024}KB fetchDelay=50ms window=4 | " +
            $"crawl={stopwatch.ElapsedMilliseconds}ms serialParse(30)={parseOnly.ElapsedMilliseconds}ms " +
            $"parallelParse(30,dop4)={parseParallel.ElapsedMilliseconds}ms " +
            $"fetchOnlyLowerBound={(int)Math.Ceiling(pageCount / 4.0) * 50 + 50}ms");

        using var db = new CrawlerDbContext(DbOptions());
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.PagesCrawled.Should().Be(pageCount + 1);
        (await db.Pages.CountAsync(p => p.JobId == job.Id && p.Status == PageStatus.Crawled)).Should().Be(pageCount + 1);
    }

    private DbContextOptions<CrawlerDbContext> DbOptions(SavedPagesRecorder? recorder = null)
    {
        var builder = new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite(_connection);
        if (recorder is not null) builder.AddInterceptors(recorder);
        return builder.Options;
    }

    private async Task<CrawlJob> SeedJobAsync(string url, int maxDepth, int maxPages)
    {
        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            InputUrl = url,
            StartingDomain = new Uri(url).Host,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = JobStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        using var db = new CrawlerDbContext(DbOptions());
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task RunEngineAsync(CrawlJob job, IPageFetcher fetcher, int maxConcurrentFetches, SavedPagesRecorder? recorder = null)
    {
        var options = new CrawlOptions { MaxConcurrentFetches = maxConcurrentFetches, ProgressFlushEveryPages = 5 };
        var message = new CrawlJobRequested(job.Id, job.InputUrl, job.MaxDepth, job.MaxPages, DateTime.UtcNow);
        using var db = new CrawlerDbContext(DbOptions(recorder));
        var engine = new CrawlEngine(db, fetcher, Options.Create(options), NullLogger<CrawlEngine>.Instance);
        await engine.RunAsync(message, CancellationToken.None);
    }

    /// <summary>Records, per SaveChanges, the fetched pages being written (Modified with a CrawledAt) — i.e. the persistence order.</summary>
    private sealed class SavedPagesRecorder : SaveChangesInterceptor
    {
        public ConcurrentQueue<string> Urls { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<CrawlPage>())
            {
                if (entry.State == EntityState.Modified && entry.Entity.CrawledAt is not null) Urls.Enqueue(entry.Entity.Url);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>A root linking to N heavy pages (~2000 anchors each) served after a fixed delay.</summary>
    private sealed class SyntheticSiteFetcher : IPageFetcher
    {
        public const string Root = "http://bench.test/";
        private readonly string _rootHtml;
        private readonly TimeSpan _delay;

        public SyntheticSiteFetcher(int pageCount, TimeSpan delay)
        {
            _delay = delay;
            var root = new StringBuilder("<html><head><title>root</title></head><body>");
            for (var i = 0; i < pageCount; i++) root.Append("<a href=\"/p").Append(i).Append("\">p</a>");
            _rootHtml = root.Append("</body></html>").ToString();

            var page = new StringBuilder("<html><head><title>page</title></head><body>");
            for (var i = 0; i < 2000; i++)
            {
                page.Append("<div class=\"item\"><p>Lorem ipsum dolor sit amet, consectetur adipiscing elit.</p>")
                    .Append("<a href=\"/x/").Append(i).Append("?q=").Append(i % 7).Append("\">link</a>")
                    .Append("<a href=\"https://ext").Append(i % 50).Append(".example/\">ext</a></div>");
            }
            PageHtml = page.Append("</body></html>").ToString();
        }

        public string PageHtml { get; }

        public async Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return FetchResult.Ok(200, url.AbsolutePath == "/" ? _rootHtml : PageHtml);
        }
    }
}

using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Tests;

/// <summary>
/// Integration-style tests: real CrawlEngine + real EF Core (SQLite in-memory) + local HTML fixtures.
/// </summary>
public sealed class CrawlEngineTests : IDisposable
{
    private const string RootUrl = "http://site.test/";
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CrawlerDbContext> _dbOptions;

    public CrawlEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite(_connection).Options;
        using var db = new CrawlerDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task RunAsync_CrawlsFixtureSite_PersistsPagesEdgesAndRatios()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        var fetcher = new FixturePageFetcher();

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.StartedAt.Should().NotBeNull();
        saved.CompletedAt.Should().NotBeNull();
        saved.PagesCrawled.Should().Be(7);
        saved.PagesDiscovered.Should().Be(7);

        var pages = await db.Pages.Where(p => p.JobId == job.Id).OrderBy(p => p.Depth).ThenBy(p => p.Id).ToListAsync();
        pages.Should().HaveCount(7, "external links and links beyond max depth are not stored as pages");
        pages.Select(p => p.Url).Should().OnlyHaveUniqueItems();

        var root = pages.Single(p => p.Depth == 0);
        root.Url.Should().Be("http://site.test/");
        root.ParentPageId.Should().BeNull();
        root.Status.Should().Be(PageStatus.Crawled);
        root.Title.Should().Be("Fixture Home");
        root.OutgoingLinkCount.Should().Be(5, "mailto/tel/javascript are ignored and fragment duplicates collapse");
        root.InternalLinkCount.Should().Be(4);
        root.DomainLinkRatio.Should().BeApproximately(0.8, 1e-9);
        root.OutgoingLinks.Should().Equal(
            "http://site.test/about",
            "http://site.test/products",
            "https://external.example.org/",
            "http://site.test/image.png",
            "http://site.test/missing");

        var about = pages.Single(p => p.Url == "http://site.test/about");
        about.Depth.Should().Be(1);
        about.ParentPageId.Should().Be(root.Id);
        about.DomainLinkRatio.Should().BeApproximately(0.6, 1e-9);

        pages.Single(p => p.Url == "http://site.test/products").DomainLinkRatio.Should().Be(1.0);
        pages.Single(p => p.Url == "http://site.test/image.png").Status.Should().Be(PageStatus.Skipped);

        var missing = pages.Single(p => p.Url == "http://site.test/missing");
        missing.Status.Should().Be(PageStatus.Failed);
        missing.HttpStatusCode.Should().Be(404);
        missing.DomainLinkRatio.Should().Be(0);

        var deep = pages.Single(p => p.Url == "http://site.test/deep");
        deep.Depth.Should().Be(2);
        deep.Status.Should().Be(PageStatus.Crawled);
        deep.OutgoingLinkCount.Should().Be(1, "links at max depth are still counted for the ratio");
        pages.Should().NotContain(p => p.Url == "http://site.test/deeper", "children beyond max depth are not created");

        var edges = await db.Edges.Where(e => e.JobId == job.Id).ToListAsync();
        edges.Should().HaveCount(9);
        edges.Select(e => (e.ParentPageId, e.ChildPageId)).Should().OnlyHaveUniqueItems();
        edges.Should().Contain(e => e.ParentPageId == about.Id && e.ChildPageId == root.Id, "back-links to known pages are recorded as edges");

        fetcher.Fetched.Select(u => u.ToString()).Should().OnlyHaveUniqueItems("a normalized URL is fetched at most once per job");
        fetcher.Fetched.Should().NotContain(u => u.Host == "external.example.org");
    }

    [Fact]
    public async Task RunAsync_DeliveredTwice_DoesNotDuplicatePagesOrEdges()
    {
        var job = await SeedJobAsync(maxDepth: 2);

        await RunEngineAsync(job, new FixturePageFetcher());
        var (pagesAfterFirst, edgesAfterFirst) = await CountRowsAsync(job.Id);

        await RunEngineAsync(job, new FixturePageFetcher());
        var (pagesAfterSecond, edgesAfterSecond) = await CountRowsAsync(job.Id);

        pagesAfterSecond.Should().Be(pagesAfterFirst);
        edgesAfterSecond.Should().Be(edgesAfterFirst);
    }

    [Fact]
    public async Task RunAsync_RedeliveredWhileRunning_ResumesWithoutDuplicates()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        await RunEngineAsync(job, new FixturePageFetcher());

        // Simulate a worker crash mid-job: job still Running, one page not yet processed.
        using (var db = NewDb())
        {
            var running = await db.Jobs.SingleAsync(j => j.Id == job.Id);
            running.Status = JobStatus.Running;
            running.CompletedAt = null;
            var page = await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/products");
            page.Status = PageStatus.Discovered;
            await db.SaveChangesAsync();
        }

        var fetcher = new FixturePageFetcher();
        await RunEngineAsync(job, fetcher);

        using var verify = NewDb();
        (await verify.Jobs.SingleAsync(j => j.Id == job.Id)).Status.Should().Be(JobStatus.Completed);
        var (pages, edges) = await CountRowsAsync(job.Id);
        pages.Should().Be(7);
        edges.Should().Be(9);
        fetcher.Fetched.Should().ContainSingle(u => u.AbsolutePath == "/products");
    }

    [Fact]
    public async Task RunAsync_MaxPagesReached_StopsAndCompletes()
    {
        var job = await SeedJobAsync(maxDepth: 2, maxPages: 3);
        var fetcher = new FixturePageFetcher();

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.PagesCrawled.Should().Be(3);
        fetcher.Fetched.Should().HaveCount(3);
        (await db.Pages.CountAsync(p => p.JobId == job.Id)).Should().Be(3, "the page budget bounds stored rows, not just fetches");
        saved.PagesDiscovered.Should().Be(3);
        (await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Depth == 0)).OutgoingLinkCount
            .Should().Be(5, "link counts come from the parsed page, not from stored rows");
    }

    [Fact]
    public async Task RunAsync_RootFetchFails_MarksJobFailed()
    {
        var job = await SeedJobAsync(maxDepth: 2, url: "http://site.test/missing");

        await RunEngineAsync(job, new FixturePageFetcher());

        using var db = NewDb();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Failed);
        saved.FailureReason.Should().NotBeNullOrWhiteSpace();
        saved.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_JobCanceledMidCrawl_StopsAndKeepsCanceledStatus()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        var fetcher = new FixturePageFetcher(onFetch: async url =>
        {
            if (url.AbsolutePath != "/about") return;
            using var db = NewDb();
            await db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Canceled));
        });

        await RunEngineAsync(job, fetcher, cancellationCheckEveryPages: 1);

        using var verify = NewDb();
        var saved = await verify.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Canceled);
        fetcher.Fetched.Count.Should().BeLessThan(7);
    }

    [Fact]
    public async Task RunAsync_RootRedirectsToOtherHost_AdoptsFinalHostAsStartingDomain()
    {
        var job = await SeedJobAsync(maxDepth: 1);
        var fetcher = new FixturePageFetcher(redirects: new Dictionary<string, string> { [RootUrl] = "http://www.site.test/" });

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.StartingDomain.Should().Be("www.site.test");

        var pages = await db.Pages.Where(p => p.JobId == job.Id).ToListAsync();
        var root = pages.Single(p => p.Depth == 0);
        root.Url.Should().Be(RootUrl, "the requested URL stays the page identity");
        root.FinalUrl.Should().Be("http://www.site.test/");
        root.InternalLinkCount.Should().Be(4, "relative links resolve against the final URL and count as internal");

        pages.Single(p => p.Url == "http://www.site.test/about").Status.Should().Be(PageStatus.Crawled);
        pages.Single(p => p.Url == "http://www.site.test/products").Status.Should().Be(PageStatus.Crawled);
        pages.Where(p => p.Depth == 1).Should().OnlyContain(p => p.FinalUrl == null, "non-redirected pages record no final URL");
        pages.Should().NotContain(p => p.Url.StartsWith("http://site.test/") && p.Depth > 0);
    }

    [Fact]
    public async Task RunAsync_AlreadyCompleted_IsNoOp()
    {
        var job = await SeedJobAsync(maxDepth: 2, status: JobStatus.Completed);
        var fetcher = new FixturePageFetcher();

        await RunEngineAsync(job, fetcher);

        fetcher.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_UnknownJob_Throws()
    {
        var message = new CrawlJobRequested(Guid.NewGuid(), RootUrl, 2, 200, DateTime.UtcNow);
        using var db = NewDb();
        var engine = new CrawlEngine(db, new FixturePageFetcher(), Options.Create(new CrawlOptions()), NullLogger<CrawlEngine>.Instance);

        var act = () => engine.RunAsync(message, CancellationToken.None);

        await act.Should().ThrowAsync<JobNotFoundException>();
    }

    [Theory]
    [InlineData("http://site.test/docs/", null)]
    [InlineData("http://site.test/docs", "http://site.test/docs/")]
    public async Task RunAsync_DirectoryUrl_ResolvesRelativeLinksAgainstTrailingSlash(string url, string? redirectTo)
    {
        var job = await SeedJobAsync(maxDepth: 1, url: url);
        var redirects = redirectTo is null ? null : new Dictionary<string, string> { [url] = redirectTo };

        await RunEngineAsync(job, new FixturePageFetcher(redirects: redirects));

        using var db = NewDb();
        var pages = await db.Pages.Where(p => p.JobId == job.Id).ToListAsync();
        pages.Single(p => p.Depth == 0).FinalUrl.Should().Be(redirectTo);
        var child = pages.Single(p => p.Depth == 1);
        child.Url.Should().Be("http://site.test/docs/intro", "'intro' is relative to the directory /docs/, not to /");
        child.Status.Should().Be(PageStatus.Crawled);
    }

    [Fact]
    public async Task RunAsync_PageRedirectsToKnownPage_IsSkippedAsAlias()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        var fetcher = new FixturePageFetcher(redirects: new Dictionary<string, string>
        {
            ["http://site.test/missing"] = "http://site.test/about"
        });

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.Should().Be(JobStatus.Completed);
        var alias = await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/missing");
        alias.Status.Should().Be(PageStatus.Skipped);
        alias.Error.Should().Be("Redirects to an already known page");
        alias.OutgoingLinkCount.Should().Be(0, "an alias's links are not counted or stored");
        (await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/about")).Status.Should().Be(PageStatus.Crawled);
        (await db.Edges.CountAsync(e => e.JobId == job.Id && e.ParentPageId == alias.Id)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_LinkToFinalUrlOfAlreadyFetchedRedirect_AddsEdgeInsteadOfSecondPage()
    {
        // /guide is fetched first and redirects to /guide/; /guide/next later links to /guide/.
        var job = await SeedJobAsync(maxDepth: 3, url: "http://site.test/start");
        var fetcher = new FixturePageFetcher(redirects: new Dictionary<string, string>
        {
            ["http://site.test/guide"] = "http://site.test/guide/"
        });

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        var pages = await db.Pages.Where(p => p.JobId == job.Id).ToListAsync();
        pages.Select(p => p.Url).Should().BeEquivalentTo("http://site.test/start", "http://site.test/guide", "http://site.test/guide/next");
        pages.Should().OnlyContain(p => p.Status == PageStatus.Crawled);
        var guide = pages.Single(p => p.Url == "http://site.test/guide");
        guide.FinalUrl.Should().Be("http://site.test/guide/");
        var next = pages.Single(p => p.Url == "http://site.test/guide/next");
        (await db.Edges.CountAsync(e => e.JobId == job.Id)).Should().Be(3);
        (await db.Edges.CountAsync(e => e.ParentPageId == next.Id && e.ChildPageId == guide.Id))
            .Should().Be(1, "a link to /guide/ is an edge to the page that redirected there");
        fetcher.Fetched.Select(u => u.AbsoluteUri).Should().NotContain("http://site.test/guide/");

        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.PagesDiscovered.Should().Be(3);
        saved.PagesCrawled.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_FinalUrlAlreadyDiscoveredBeforeRedirectFetched_IsSkippedAsAlias()
    {
        // /guide/ is a Discovered row before /guide is fetched and redirects there.
        var job = await SeedJobAsync(maxDepth: 1, url: "http://site.test/start-both");
        var fetcher = new FixturePageFetcher(redirects: new Dictionary<string, string>
        {
            ["http://site.test/guide"] = "http://site.test/guide/"
        });

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        var pages = await db.Pages.Where(p => p.JobId == job.Id).ToListAsync();
        pages.Should().HaveCount(3);
        pages.Single(p => p.Url == "http://site.test/guide/").Status.Should().Be(PageStatus.Crawled);
        var alias = pages.Single(p => p.Url == "http://site.test/guide");
        alias.Status.Should().Be(PageStatus.Skipped);
        alias.Error.Should().Be("Redirects to an already known page");
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.PagesDiscovered.Should().Be(3);
        saved.PagesCrawled.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_LeaseHeldByLiveWorker_ThrowsWithRetryAfter()
    {
        var job = await SeedJobAsync(maxDepth: 2, status: JobStatus.Running, leaseOwner: "other", leaseExpiresAt: DateTime.UtcNow.AddSeconds(60));
        var fetcher = new FixturePageFetcher();

        var act = () => RunEngineAsync(job, fetcher);

        var thrown = await act.Should().ThrowAsync<JobLeaseHeldException>();
        thrown.Which.RetryAfter.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        fetcher.Fetched.Should().BeEmpty();
        using var db = NewDb();
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).LeaseOwner.Should().Be("other");
    }

    [Fact]
    public async Task RunAsync_LeaseExpired_TakesOverAndCompletes()
    {
        var job = await SeedJobAsync(maxDepth: 2, status: JobStatus.Running, leaseOwner: "dead-worker", leaseExpiresAt: DateTime.UtcNow.AddSeconds(-1));

        await RunEngineAsync(job, new FixturePageFetcher());

        using var db = NewDb();
        var saved = await db.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.PagesCrawled.Should().Be(7);
        saved.LeaseOwner.Should().BeNull("a terminal transition releases the lease");
        saved.LeaseExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_CanceledWhileFetchingLastPage_EndsCanceledNotCompleted()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        FixturePageFetcher fetcher = null!;
        fetcher = new FixturePageFetcher(onFetch: async _ =>
        {
            if (fetcher.Fetched.Count < 7) return;
            using var db = NewDb();
            await db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Canceled));
        });

        // No periodic cancellation check or flush (a flush also detects a cancel): only the final, conditional
        // transition can notice it.
        await RunEngineAsync(job, fetcher, cancellationCheckEveryPages: 1000, progressFlushEveryPages: 1000);

        using var verify = NewDb();
        var saved = await verify.Jobs.SingleAsync(j => j.Id == job.Id);
        fetcher.Fetched.Should().HaveCount(7);
        saved.Status.Should().Be(JobStatus.Canceled);
        saved.CompletedAt.Should().NotBeNull();
        saved.PagesCrawled.Should().Be(7, "the cancel is settled with the final counters");
        saved.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_DocumentProcessingThrows_FailsOnlyThatPage()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        // A relative "final URL" makes the engine throw while processing the document.
        var fetcher = new OverridingFetcher(new FixturePageFetcher(), url => url.AbsolutePath == "/about"
            ? FetchResult.Ok(200, "<a href=\"/x\">x</a>", new Uri("relative/path", UriKind.Relative))
            : null);

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.Should().Be(JobStatus.Completed);
        var about = await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/about");
        about.Status.Should().Be(PageStatus.Failed);
        about.Error.Should().Be("Unexpected error");
        about.HttpStatusCode.Should().Be(200);
        about.OutgoingLinkCount.Should().Be(0);
        (await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/products")).Status.Should().Be(PageStatus.Crawled);
    }

    [Fact]
    public void FetchResult_OnlyNotHtmlFactory_SetsIsNotHtml()
    {
        FetchResult.NotHtml(200, "image/png").IsNotHtml.Should().BeTrue();
        FetchResult.Failed(500, "Not HTML: lookalike").IsNotHtml.Should().BeFalse();
        FetchResult.Ok(200, "<p/>").IsNotHtml.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_FailureWhoseTextLooksLikeNotHtml_IsFailedNotSkipped()
    {
        var job = await SeedJobAsync(maxDepth: 1);
        var fetcher = new OverridingFetcher(new FixturePageFetcher(), url => url.AbsolutePath == "/about"
            ? FetchResult.Failed(null, "Not HTML: but actually a failure")
            : null);

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        (await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/about")).Status.Should().Be(PageStatus.Failed);
        (await db.Pages.SingleAsync(p => p.JobId == job.Id && p.Url == "http://site.test/image.png")).Status.Should().Be(PageStatus.Skipped);
    }

    [Fact]
    public async Task RunAsync_TitleWithSurrogatePairAtLimitAndNul_StoresValidTruncatedText()
    {
        var job = await SeedJobAsync(maxDepth: 0);
        // 511 chars + an emoji (2 UTF-16 units) crosses the 512 limit inside the pair; &#0; decodes to NUL.
        var title = new string('t', 511) + "\U0001F600" + "tail";
        var fetcher = new OverridingFetcher(new FixturePageFetcher(),
            _ => FetchResult.Ok(200, $"<title>&#0;{title}</title><a href=\"/about\">a</a>"));

        await RunEngineAsync(job, fetcher);

        using var db = NewDb();
        (await db.Jobs.SingleAsync(j => j.Id == job.Id)).Status.Should().Be(JobStatus.Completed);
        var root = await db.Pages.SingleAsync(p => p.JobId == job.Id);
        root.Title.Should().Be(new string('t', 511), "the pair is dropped whole and the NUL removed");
    }

    [Fact]
    public async Task RunAsync_CanceledByApiMidCrawl_SettlesWithFinalCountersAndReleasesLease()
    {
        var job = await SeedJobAsync(maxDepth: 2);
        var fetcher = new FixturePageFetcher(onFetch: async url =>
        {
            if (url.AbsolutePath != "/about") return;
            // Exactly what JobService.CancelAsync writes: status and completion time, the lease is left alone.
            using var db = NewDb();
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CompletedAt, DateTime.UtcNow));
        });

        // Cancellation is only ever checked through the progress flush here.
        await RunEngineAsync(job, fetcher, cancellationCheckEveryPages: 1000);

        using var verify = NewDb();
        var saved = await verify.Jobs.SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Canceled);
        fetcher.Fetched.Count.Should().BeLessThan(7);
        var processed = await verify.Pages.CountAsync(p => p.JobId == job.Id && p.Status != PageStatus.Discovered);
        saved.PagesCrawled.Should().Be(processed, "the worker settles the cancel with its final counters");
        saved.PagesDiscovered.Should().Be(await verify.Pages.CountAsync(p => p.JobId == job.Id));
        saved.LeaseOwner.Should().BeNull();
        saved.LeaseExpiresAt.Should().BeNull();
    }

    private CrawlerDbContext NewDb() => new(_dbOptions);

    private async Task<CrawlJob> SeedJobAsync(
        int maxDepth, int maxPages = 200, string url = RootUrl, JobStatus status = JobStatus.Pending,
        string? leaseOwner = null, DateTime? leaseExpiresAt = null)
    {
        var job = new CrawlJob
        {
            Id = Guid.NewGuid(),
            InputUrl = url,
            StartingDomain = FixturePageFetcher.Host,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt
        };
        using var db = NewDb();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task RunEngineAsync(
        CrawlJob job, IPageFetcher fetcher, int cancellationCheckEveryPages = 5, int progressFlushEveryPages = 1)
    {
        var options = new CrawlOptions
        {
            MaxConcurrentFetches = 2,
            ProgressFlushEveryPages = progressFlushEveryPages,
            CancellationCheckEveryPages = cancellationCheckEveryPages
        };
        var message = new CrawlJobRequested(job.Id, job.InputUrl, job.MaxDepth, job.MaxPages, DateTime.UtcNow);
        using var db = NewDb();
        var engine = new CrawlEngine(db, fetcher, Options.Create(options), NullLogger<CrawlEngine>.Instance);
        await engine.RunAsync(message, CancellationToken.None);
    }

    private async Task<(int Pages, int Edges)> CountRowsAsync(Guid jobId)
    {
        using var db = NewDb();
        return (await db.Pages.CountAsync(p => p.JobId == jobId), await db.Edges.CountAsync(e => e.JobId == jobId));
    }

    /// <summary>Returns a custom result for selected URLs and delegates everything else to the fixture site.</summary>
    private sealed class OverridingFetcher(IPageFetcher inner, Func<Uri, FetchResult?> overrideFor) : IPageFetcher
    {
        public Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken)
            => overrideFor(url) is { } result ? Task.FromResult(result) : inner.FetchAsync(url, cancellationToken);
    }
}

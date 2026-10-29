using System.Net;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Crawler.Api.Tests;

public sealed class CreateJobEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public CreateJobEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Post_ValidRequest_Returns201WithLocationAndJobId()
    {
        var response = await _client.PostJsonAsync("/api/jobs", new { url = "https://example.com/" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.JsonAsync();
        var jobId = Guid.Parse(json["jobId"]!.GetValue<string>());
        response.Headers.Location!.ToString().Should().Be($"/api/jobs/{jobId}");
    }

    [Fact]
    public async Task Post_ValidRequest_PersistsPendingJobWithNormalizedUrlAndDefaults()
    {
        var response = await _client.PostJsonAsync("/api/jobs", new { url = "HTTP://Example.COM/a/#x" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jobId = Guid.Parse((await response.JsonAsync())["jobId"]!.GetValue<string>());

        var job = await _factory.WithDbAsync(db => db.Jobs.SingleAsync(j => j.Id == jobId));
        job.Status.Should().Be(JobStatus.Pending);
        // Trailing slashes are significant (kept); fragment dropped, scheme/host lowercased.
        job.InputUrl.Should().Be("http://example.com/a/");
        job.StartingDomain.Should().Be("example.com");
        job.MaxDepth.Should().Be(2);
        job.MaxPages.Should().Be(200);
        job.FailureReason.Should().BeNull();
        job.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Post_IdnUrl_StoresPunycodeStartingDomain()
    {
        var response = await _client.PostJsonAsync("/api/jobs", new { url = "https://user:pw@BÜCHER.example/" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var job = await _factory.WithDbAsync(db => db.Jobs.SingleAsync());
        job.StartingDomain.Should().Be("xn--bcher-kva.example");
        job.InputUrl.Should().Be("https://xn--bcher-kva.example/");
    }

    [Fact]
    public async Task Post_UrlWithSpace_StoredInputUrlRenormalizesToEscapedForm()
    {
        await _client.PostJsonAsync("/api/jobs", new { url = "https://example.com/a b" });

        var job = await _factory.WithDbAsync(db => db.Jobs.SingleAsync());
        // The worker re-normalizes InputUrl and uses AbsoluteUri as the page identity.
        Crawler.Domain.Crawling.UrlNormalizer.Normalize(job.InputUrl).Should().Be("https://example.com/a%20b");
    }

    [Fact]
    public async Task Post_ValidRequest_PublishesCrawlJobRequestedWithMatchingFields()
    {
        var response = await _client.PostJsonAsync("/api/jobs", new { url = "https://example.com/start", maxDepth = 3, maxPages = 50 });
        var jobId = Guid.Parse((await response.JsonAsync())["jobId"]!.GetValue<string>());

        await _factory.Publisher.Received(1).PublishAsync(
            Arg.Is<CrawlJobRequested>(m =>
                m.JobId == jobId && m.Url == "https://example.com/start" && m.MaxDepth == 3 && m.MaxPages == 50),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_DefaultsApplied_PublishedMessageCarriesDefaults()
    {
        await _client.PostJsonAsync("/api/jobs", new { url = "https://example.com/" });

        await _factory.Publisher.Received(1).PublishAsync(
            Arg.Is<CrawlJobRequested>(m => m.MaxDepth == 2 && m.MaxPages == 200),
            Arg.Is<string>(c => !string.IsNullOrEmpty(c)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1000)]
    public async Task Post_BoundaryLimits_AreAccepted(int maxDepth, int maxPages)
    {
        var response = await _client.PostJsonAsync("/api/jobs", new { url = "https://example.com/", maxDepth, maxPages });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    public static TheoryData<object, string> InvalidRequests => new()
    {
        { new { }, "url" },
        { new { url = (string?)null }, "url" },
        { new { url = "" }, "url" },
        { new { url = "not a url" }, "url" },
        { new { url = "/relative/path" }, "url" },
        { new { url = "ftp://example.com/file" }, "url" },
        { new { url = "mailto:a@b.com" }, "url" },
        { new { url = "https://example.com/", maxDepth = 0 }, "maxDepth" },
        { new { url = "https://example.com/", maxDepth = 6 }, "maxDepth" },
        { new { url = "https://example.com/", maxPages = 0 }, "maxPages" },
        { new { url = "https://example.com/", maxPages = 1001 }, "maxPages" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Post_InvalidRequest_Returns400ValidationProblemWithErrorKey(object body, string expectedKey)
    {
        var response = await _client.PostJsonAsync("/api/jobs", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var json = await response.JsonAsync();
        json["errors"].Keys().Should().Equal(expectedKey);

        await _factory.Publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
        (await _factory.WithDbAsync(db => db.Jobs.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Post_SeveralInvalidFields_ReportsAllErrorKeys()
    {
        var response = await _client.PostJsonAsync("/api/jobs", new { url = "nope", maxDepth = 99, maxPages = -1 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync())["errors"].Keys().Should().BeEquivalentTo("url", "maxDepth", "maxPages");
    }

    [Fact]
    public async Task Post_PublisherThrows_Returns201AndLeavesJobPendingForReconciler()
    {
        _factory.Publisher
            .PublishAsync(Arg.Any<CrawlJobRequested>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var response = await _client.PostJsonAsync("/api/jobs", new { url = "https://example.com/" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var jobId = Guid.Parse((await response.JsonAsync())["jobId"]!.GetValue<string>());
        var job = await _factory.WithDbAsync(db => db.Jobs.SingleAsync());
        job.Id.Should().Be(jobId);
        job.Status.Should().Be(JobStatus.Pending, "the reconciler re-publishes stale Pending jobs");
        job.FailureReason.Should().BeNull();
        job.CompletedAt.Should().BeNull();
    }
}

using System.Net;
using Crawler.Domain.Entities;
using FluentAssertions;

namespace Crawler.Api.Tests;

public sealed class GetJobEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public GetJobEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CrawlJob> SeedAsync(CrawlJob job)
    {
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); return db.SaveChangesAsync(); });
        return job;
    }

    [Fact]
    public async Task Get_UnknownId_Returns404ProblemDetails()
    {
        var id = Guid.NewGuid();
        var response = await _client.GetAsync($"/api/jobs/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var json = await response.JsonAsync();
        json["status"]!.GetValue<int>().Should().Be(404);
        json["title"]!.GetValue<string>().Should().Be("Job not found");
        json["detail"]!.GetValue<string>().Should().Contain(id.ToString());
    }

    [Fact]
    public async Task Get_NonGuidId_Returns404()
    {
        var response = await _client.GetAsync("/api/jobs/not-a-guid");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_ExistingJob_Returns200WithFullShape()
    {
        var started = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var job = await SeedAsync(Seed.Job(
            JobStatus.Completed, "https://example.com/", crawled: 7, discovered: 9, maxPages: 50, maxDepth: 3,
            startedAt: started, completedAt: started.AddMilliseconds(1500)));

        var response = await _client.GetAsync($"/api/jobs/{job.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        json["jobId"]!.GetValue<string>().Should().Be(job.Id.ToString());
        json["inputUrl"]!.GetValue<string>().Should().Be("https://example.com/");
        json["startingDomain"]!.GetValue<string>().Should().Be("example.com");
        json["maxDepth"]!.GetValue<int>().Should().Be(3);
        json["maxPages"]!.GetValue<int>().Should().Be(50);
        json["status"]!.GetValue<string>().Should().Be("Completed");
        json["failureReason"].Should().BeNull();
        json["pagesCrawled"]!.GetValue<int>().Should().Be(7);
        json["pagesDiscovered"]!.GetValue<int>().Should().Be(9);
        json["progressPercent"]!.GetValue<int>().Should().Be(100);
        json["durationMs"]!.GetValue<long>().Should().Be(1500);
        json["createdAt"].Should().NotBeNull();
        json["startedAt"].Should().NotBeNull();
        json["completedAt"].Should().NotBeNull();
    }

    [Theory]
    [InlineData(JobStatus.Pending, 0, 0, 0)]
    [InlineData(JobStatus.Running, 10, 40, 25)]
    [InlineData(JobStatus.Completed, 3, 10, 100)]
    [InlineData(JobStatus.Failed, 1, 1, 100)]
    [InlineData(JobStatus.Canceled, 5, 10, 50)]
    public async Task Get_ProgressPercent_DependsOnStatusAndCounters(JobStatus status, int crawled, int discovered, int expected)
    {
        var job = await SeedAsync(Seed.Job(status, crawled: crawled, discovered: discovered));

        var json = await (await _client.GetAsync($"/api/jobs/{job.Id}")).JsonAsync();

        json["progressPercent"]!.GetValue<int>().Should().Be(expected);
        json["status"]!.GetValue<string>().Should().Be(status.ToString());
    }

    [Fact]
    public async Task Get_PendingJob_DurationIsNull()
    {
        var job = await SeedAsync(Seed.Job(JobStatus.Pending));
        var json = await (await _client.GetAsync($"/api/jobs/{job.Id}")).JsonAsync();
        json["durationMs"].Should().BeNull();
    }

    [Fact]
    public async Task Get_RunningJob_DurationMeasuredUntilNow()
    {
        var job = await SeedAsync(Seed.Job(JobStatus.Running, startedAt: DateTime.UtcNow.AddSeconds(-5)));
        var json = await (await _client.GetAsync($"/api/jobs/{job.Id}")).JsonAsync();
        json["durationMs"]!.GetValue<long>().Should().BeInRange(5000, 60_000);
    }

    [Fact]
    public async Task Get_FailedJob_ExposesFailureReason()
    {
        var job = Seed.Job(JobStatus.Failed);
        job.FailureReason = "boom";
        await SeedAsync(job);

        var json = await (await _client.GetAsync($"/api/jobs/{job.Id}")).JsonAsync();
        json["failureReason"]!.GetValue<string>().Should().Be("boom");
    }
}

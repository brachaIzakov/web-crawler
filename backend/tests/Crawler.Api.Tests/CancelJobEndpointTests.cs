using System.Net;
using Crawler.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Tests;

public sealed class CancelJobEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public CancelJobEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CrawlJob> SeedAsync(JobStatus status)
    {
        var job = Seed.Job(status);
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); return db.SaveChangesAsync(); });
        return job;
    }

    private Task<CrawlJob> ReloadAsync(Guid id)
        => _factory.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Running)]
    public async Task Cancel_ActiveJob_Returns202AndMarksCanceled(JobStatus status)
    {
        var job = await SeedAsync(status);

        var response = await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().Should().Be($"/api/jobs/{job.Id}");
        var json = await response.JsonAsync();
        json["jobId"]!.GetValue<string>().Should().Be(job.Id.ToString());
        json["status"]!.GetValue<string>().Should().Be("Canceled");

        var saved = await ReloadAsync(job.Id);
        saved.Status.Should().Be(JobStatus.Canceled);
        saved.CompletedAt.Should().NotBeNull();
        saved.CompletedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Cancel_RunningJob_KeepsWorkerLeaseSoTheWorkerCanSettleIt()
    {
        var job = Seed.Job(JobStatus.Running);
        job.LeaseOwner = "worker-1";
        job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(2);
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); return db.SaveChangesAsync(); });

        var response = await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var saved = await ReloadAsync(job.Id);
        saved.Status.Should().Be(JobStatus.Canceled);
        saved.LeaseOwner.Should().Be("worker-1", "the worker's final write is filtered by its lease");
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Canceled)]
    public async Task Cancel_TerminalJob_Returns409AndLeavesJobUntouched(JobStatus status)
    {
        var job = await SeedAsync(status);

        var response = await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.JsonAsync())["title"]!.GetValue<string>().Should().Be("Job already finished");
        (await ReloadAsync(job.Id)).Status.Should().Be(status);
    }

    [Fact]
    public async Task Cancel_Twice_SecondCallIsConflict()
    {
        var job = await SeedAsync(JobStatus.Running);

        (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await _client.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_UnknownJob_Returns404()
    {
        var response = await _client.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.JsonAsync())["title"]!.GetValue<string>().Should().Be("Job not found");
    }

    [Fact]
    public async Task Cancel_DoesNotAffectOtherJobs()
    {
        var target = await SeedAsync(JobStatus.Pending);
        var bystander = await SeedAsync(JobStatus.Pending);

        await _client.PostAsync($"/api/jobs/{target.Id}/cancel", null);

        (await ReloadAsync(bystander.Id)).Status.Should().Be(JobStatus.Pending);
    }
}

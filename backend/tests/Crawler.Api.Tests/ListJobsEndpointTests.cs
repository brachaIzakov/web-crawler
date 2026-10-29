using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Crawler.Api.Tests;

public sealed class ListJobsEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly List<Guid> _newestFirst = new();

    public ListJobsEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetAsync();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Inserted in scrambled order so the listing order cannot come from insertion order.
        var jobs = new[] { 3, 0, 4, 1, 2 }
            .Select(i => Seed.Job(url: $"https://site{i}.example/", createdAt: t0.AddMinutes(i)))
            .ToList();
        await _factory.WithDbAsync(db => { db.Jobs.AddRange(jobs); return db.SaveChangesAsync(); });
        _newestFirst.AddRange(jobs.OrderByDescending(j => j.CreatedAt).Select(j => j.Id));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Guid[] Ids(JsonNode json)
        => json["items"]!.AsArray().Select(i => Guid.Parse(i!["jobId"]!.GetValue<string>())).ToArray();

    [Fact]
    public async Task List_Defaults_ReturnsAllMostRecentFirst()
    {
        var response = await _client.GetAsync("/api/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        Ids(json).Should().Equal(_newestFirst);
        json["totalCount"]!.GetValue<int>().Should().Be(5);
        json["page"]!.GetValue<int>().Should().Be(1);
        json["pageSize"]!.GetValue<int>().Should().Be(20);
    }

    [Fact]
    public async Task List_ItemShape_ContainsSummaryFields()
    {
        var json = await (await _client.GetAsync("/api/jobs?page=1&pageSize=1")).JsonAsync();
        var item = json["items"]![0]!;
        item["inputUrl"]!.GetValue<string>().Should().Be("https://site4.example/");
        item["status"]!.GetValue<string>().Should().Be("Pending");
        item["maxDepth"]!.GetValue<int>().Should().Be(2);
        item["pagesCrawled"]!.GetValue<int>().Should().Be(0);
        item["pagesDiscovered"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task List_SecondPage_ReturnsNextSlice()
    {
        var json = await (await _client.GetAsync("/api/jobs?page=2&pageSize=2")).JsonAsync();

        Ids(json).Should().Equal(_newestFirst.Skip(2).Take(2));
        json["totalCount"]!.GetValue<int>().Should().Be(5);
        json["page"]!.GetValue<int>().Should().Be(2);
        json["pageSize"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task List_LastPartialPage_ReturnsRemainder()
    {
        var json = await (await _client.GetAsync("/api/jobs?page=3&pageSize=2")).JsonAsync();
        Ids(json).Should().Equal(_newestFirst.Skip(4));
    }

    [Fact]
    public async Task List_PageBeyondLast_ReturnsEmptyItemsWithTotalCount()
    {
        var response = await _client.GetAsync("/api/jobs?page=10&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        json["items"]!.AsArray().Should().BeEmpty();
        json["totalCount"]!.GetValue<int>().Should().Be(5);
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page=-1", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task List_InvalidPaging_Returns400(string query, string expectedKey)
    {
        var response = await _client.GetAsync($"/api/jobs?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync())["errors"].Keys().Should().Equal(expectedKey);
    }

    [Fact]
    public async Task List_MaxPageSize_IsAccepted()
        => (await _client.GetAsync("/api/jobs?pageSize=100")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task List_NoJobs_ReturnsEmpty()
    {
        await _factory.ResetAsync();
        var json = await (await _client.GetAsync("/api/jobs")).JsonAsync();
        json["items"]!.AsArray().Should().BeEmpty();
        json["totalCount"]!.GetValue<int>().Should().Be(0);
    }
}

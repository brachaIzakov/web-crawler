using System.Net;
using System.Text.Json.Nodes;
using Crawler.Domain.Entities;
using FluentAssertions;

namespace Crawler.Api.Tests;

public sealed class JobTreeEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public JobTreeEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static long[] ChildIds(JsonNode node)
        => node["children"]!.AsArray().Select(c => c!["pageId"]!.GetValue<long>()).ToArray();

    [Fact]
    public async Task Tree_RootWithChildrenAndGrandchildren_ReturnsNestedStructure()
    {
        var job = Seed.Job(JobStatus.Completed, "https://example.com/");
        const string Root = "https://example.com/";
        var root = Seed.Page(job.Id, 10, Root, 0, null,
            "https://example.com/a", "https://example.com/b", "https://other.org/");
        root.InternalLinkCount = 2;
        root.DomainLinkRatio = 2d / 3d;
        root.FinalUrl = "https://www.example.com/";
        // Children inserted out of Id order; the API must order them by Id.
        var childB = Seed.Page(job.Id, 30, "https://example.com/b", 1, 10);
        var childA = Seed.Page(job.Id, 20, "https://example.com/a", 1, 10);
        var grandchild = Seed.Page(job.Id, 40, "https://example.com/a/x", 2, 20);
        grandchild.Status = PageStatus.Failed;
        grandchild.HttpStatusCode = 404;
        grandchild.Error = "HTTP 404 Not Found";
        grandchild.Title = null;

        await _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(job);
            db.Pages.Add(root);
            await db.SaveChangesAsync();
            db.Pages.AddRange(childB, childA);
            await db.SaveChangesAsync();
            db.Pages.Add(grandchild);
            await db.SaveChangesAsync();
        });

        var response = await _client.GetAsync($"/api/jobs/{job.Id}/tree");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        json["jobId"]!.GetValue<string>().Should().Be(job.Id.ToString());
        json["totalPages"]!.GetValue<int>().Should().Be(4);

        var rootNode = json["root"]!;
        rootNode["pageId"]!.GetValue<long>().Should().Be(10);
        rootNode["url"]!.GetValue<string>().Should().Be(Root);
        rootNode["finalUrl"]!.GetValue<string>().Should().Be("https://www.example.com/");
        rootNode["depth"]!.GetValue<int>().Should().Be(0);
        rootNode["status"]!.GetValue<string>().Should().Be("Crawled");
        rootNode["httpStatusCode"]!.GetValue<int>().Should().Be(200);
        rootNode["title"]!.GetValue<string>().Should().Be("Title 10");
        rootNode["outgoingLinkCount"]!.GetValue<int>().Should().Be(3);
        rootNode["internalLinkCount"]!.GetValue<int>().Should().Be(2);
        rootNode["domainLinkRatio"]!.GetValue<double>().Should().BeApproximately(2d / 3d, 1e-9);
        ChildIds(rootNode).Should().Equal(20, 30);

        var a = rootNode["children"]![0]!;
        a["finalUrl"].Should().BeNull();
        a["depth"]!.GetValue<int>().Should().Be(1);
        ChildIds(a).Should().Equal(40);

        var b = rootNode["children"]![1]!;
        ChildIds(b).Should().BeEmpty();

        var leaf = a["children"]![0]!;
        leaf["depth"]!.GetValue<int>().Should().Be(2);
        leaf["status"]!.GetValue<string>().Should().Be("Failed");
        leaf["httpStatusCode"]!.GetValue<int>().Should().Be(404);
        leaf["error"]!.GetValue<string>().Should().Be("HTTP 404 Not Found");
        leaf["title"].Should().BeNull();
        ChildIds(leaf).Should().BeEmpty();
    }

    [Fact]
    public async Task Tree_JobWithoutPages_ReturnsNullRootAndZeroTotal()
    {
        var job = Seed.Job();
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); return db.SaveChangesAsync(); });

        var response = await _client.GetAsync($"/api/jobs/{job.Id}/tree");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        json["root"].Should().BeNull();
        json["totalPages"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Tree_OnlyIncludesPagesOfRequestedJob()
    {
        var job = Seed.Job();
        var other = Seed.Job();
        await _factory.WithDbAsync(db =>
        {
            db.Jobs.AddRange(job, other);
            db.Pages.Add(Seed.Page(job.Id, 1, "https://example.com/", 0));
            db.Pages.Add(Seed.Page(other.Id, 2, "https://example.com/", 0));
            return db.SaveChangesAsync();
        });

        var json = await (await _client.GetAsync($"/api/jobs/{job.Id}/tree")).JsonAsync();

        json["totalPages"]!.GetValue<int>().Should().Be(1);
        json["root"]!["pageId"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public async Task Tree_UnknownJob_Returns404()
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/tree");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.JsonAsync())["title"]!.GetValue<string>().Should().Be("Job not found");
    }
}

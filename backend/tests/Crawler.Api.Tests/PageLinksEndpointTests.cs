using System.Net;
using FluentAssertions;

namespace Crawler.Api.Tests;

public sealed class PageLinksEndpointTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public PageLinksEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Links_ReturnsLinksWithIsInternalFromStartingDomain()
    {
        var job = Seed.Job(url: "https://example.com/");
        var page = Seed.Page(job.Id, 5, "https://example.com/", 0, null,
            "https://example.com/a", "http://EXAMPLE.com:8080/b", "https://www.example.com/", "https://other.org/");
        page.InternalLinkCount = 2;
        page.DomainLinkRatio = 0.5;
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); db.Pages.Add(page); return db.SaveChangesAsync(); });

        var response = await _client.GetAsync($"/api/jobs/{job.Id}/pages/5/links");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.JsonAsync();
        json["pageId"]!.GetValue<long>().Should().Be(5);
        json["url"]!.GetValue<string>().Should().Be("https://example.com/");
        json["outgoingLinkCount"]!.GetValue<int>().Should().Be(4);
        json["internalLinkCount"]!.GetValue<int>().Should().Be(2);
        json["domainLinkRatio"]!.GetValue<double>().Should().Be(0.5);

        var links = json["links"]!.AsArray()
            .Select(l => (Url: l!["url"]!.GetValue<string>(), Internal: l["isInternal"]!.GetValue<bool>()))
            .ToList();
        links.Should().Equal(
            ("https://example.com/a", true),
            ("http://EXAMPLE.com:8080/b", true),
            ("https://www.example.com/", false),
            ("https://other.org/", false));
    }

    [Fact]
    public async Task Links_PageWithoutLinks_ReturnsEmptyList()
    {
        var job = Seed.Job();
        await _factory.WithDbAsync(db =>
        {
            db.Jobs.Add(job);
            db.Pages.Add(Seed.Page(job.Id, 7, "https://example.com/", 0));
            return db.SaveChangesAsync();
        });

        var json = await (await _client.GetAsync($"/api/jobs/{job.Id}/pages/7/links")).JsonAsync();
        json["links"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Links_UnknownPage_Returns404()
    {
        var job = Seed.Job();
        await _factory.WithDbAsync(db => { db.Jobs.Add(job); return db.SaveChangesAsync(); });

        var response = await _client.GetAsync($"/api/jobs/{job.Id}/pages/999/links");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.JsonAsync())["title"]!.GetValue<string>().Should().Be("Page not found");
    }

    [Fact]
    public async Task Links_PageOfAnotherJob_Returns404()
    {
        var owner = Seed.Job();
        var other = Seed.Job();
        await _factory.WithDbAsync(db =>
        {
            db.Jobs.AddRange(owner, other);
            db.Pages.Add(Seed.Page(owner.Id, 3, "https://example.com/", 0, null, "https://example.com/a"));
            return db.SaveChangesAsync();
        });

        var response = await _client.GetAsync($"/api/jobs/{other.Id}/pages/3/links");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Links_UnknownJob_Returns404()
        => (await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/pages/1/links")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Links_NonNumericPageId_Returns404()
        => (await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}/pages/abc/links")).StatusCode.Should().Be(HttpStatusCode.NotFound);
}

using System.Net;
using FluentAssertions;

namespace Crawler.Api.Tests;

public sealed class RateLimitedApiFactory : ApiFactory
{
    public const int Limit = 3;
    protected override int CreateJobPerMinute => Limit;
}

public sealed class RateLimitTests(RateLimitedApiFactory factory) : IClassFixture<RateLimitedApiFactory>
{
    [Fact]
    public async Task CreateJob_OverPerClientLimit_Returns429_OtherEndpointsUnaffected()
    {
        var client = factory.CreateClient();

        for (var i = 0; i < RateLimitedApiFactory.Limit; i++)
            (await client.PostJsonAsync("/api/jobs", new { url = $"https://example.com/{i}" }))
                .StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PostJsonAsync("/api/jobs", new { url = "https://example.com/over" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Only job creation is limited.
        (await client.GetAsync("/api/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

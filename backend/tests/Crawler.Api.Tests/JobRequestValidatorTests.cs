using Crawler.Api.Contracts;
using Crawler.Api.Services;
using FluentAssertions;

namespace Crawler.Api.Tests;

public class JobRequestValidatorTests
{
    [Fact]
    public void Validate_Valid_ReturnsNormalizedRequestWithDefaults()
    {
        var errors = JobRequestValidator.Validate(new CreateJobRequest("HTTPS://Example.com:443/x/", null, null), out var validated);

        errors.Should().BeEmpty();
        validated!.Url.AbsoluteUri.Should().Be("https://example.com/x/");
        validated.MaxDepth.Should().Be(JobLimits.DefaultDepth);
        validated.MaxPages.Should().Be(JobLimits.DefaultPages);
    }

    [Theory]
    [InlineData(null, 2, 200, "url")]
    [InlineData("javascript:alert(1)", 2, 200, "url")]
    [InlineData("https://example.com/", 0, 200, "maxDepth")]
    [InlineData("https://example.com/", 6, 200, "maxDepth")]
    [InlineData("https://example.com/", -1, 200, "maxDepth")]
    [InlineData("https://example.com/", 2, 0, "maxPages")]
    [InlineData("https://example.com/", 2, 1001, "maxPages")]
    public void Validate_Invalid_ReturnsSingleErrorAndNoRequest(string? url, int depth, int pages, string key)
    {
        var errors = JobRequestValidator.Validate(new CreateJobRequest(url, depth, pages), out var validated);

        validated.Should().BeNull();
        errors.Keys.Should().Equal(key);
    }

    [Theory]
    [InlineData(1, 1, new string[0])]
    [InlineData(1, 100, new string[0])]
    [InlineData(0, 20, new[] { "page" })]
    [InlineData(1, 0, new[] { "pageSize" })]
    [InlineData(1, 101, new[] { "pageSize" })]
    [InlineData(0, 101, new[] { "page", "pageSize" })]
    public void ValidatePaging_TableDriven(int page, int pageSize, string[] expectedKeys)
        => JobRequestValidator.ValidatePaging(page, pageSize).Keys.Should().BeEquivalentTo(expectedKeys);
}

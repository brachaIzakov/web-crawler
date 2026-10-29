using Crawler.Api.Contracts;
using Crawler.Domain.Crawling;

namespace Crawler.Api.Services;

/// <summary>A create-job request that passed validation, with the URL already normalized.</summary>
public sealed record ValidatedJobRequest(Uri Url, int MaxDepth, int MaxPages);

public static class JobRequestValidator
{
    public static IDictionary<string, string[]> Validate(CreateJobRequest request, out ValidatedJobRequest? validated)
    {
        validated = null;
        var errors = new Dictionary<string, string[]>();

        var maxDepth = request.MaxDepth ?? JobLimits.DefaultDepth;
        var maxPages = request.MaxPages ?? JobLimits.DefaultPages;

        if (!UrlNormalizer.TryNormalize(request.Url, baseUri: null, out var url))
            errors["url"] = ["A valid absolute http or https URL is required."];
        else if (url.AbsoluteUri.Length > JobLimits.MaxUrlLength)
            errors["url"] = [$"url must be at most {JobLimits.MaxUrlLength} characters after normalization."];

        if (maxDepth is < JobLimits.MinDepth or > JobLimits.MaxDepth)
            errors["maxDepth"] = [$"maxDepth must be between {JobLimits.MinDepth} and {JobLimits.MaxDepth}."];

        if (maxPages is < JobLimits.MinPages or > JobLimits.MaxPages)
            errors["maxPages"] = [$"maxPages must be between {JobLimits.MinPages} and {JobLimits.MaxPages}."];

        if (errors.Count == 0)
            validated = new ValidatedJobRequest(url, maxDepth, maxPages);

        return errors;
    }

    public static IDictionary<string, string[]> ValidatePaging(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();

        if (page is < 1 or > JobLimits.MaxPage)
            errors["page"] = [$"page must be between 1 and {JobLimits.MaxPage}."];

        if (pageSize is < 1 or > JobLimits.MaxPageSize)
            errors["pageSize"] = [$"pageSize must be between 1 and {JobLimits.MaxPageSize}."];

        return errors;
    }
}

using Crawler.Api.Contracts;
using Crawler.Api.Http;
using Crawler.Api.Services;
using Crawler.Api.Streaming;

namespace Crawler.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        var jobs = routes.MapGroup("/api/jobs").WithTags("Jobs");

        jobs.MapPost("/", CreateJob)
            .RequireRateLimiting(RateLimitPolicies.CreateJob)
            .Produces<CreateJobResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        jobs.MapGet("/", ListJobs)
            .Produces<PagedResult<JobSummary>>()
            .ProducesValidationProblem();

        jobs.MapGet("/{id:guid}", GetJob)
            .Produces<JobDetails>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/events", StreamJobEvents)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/tree", GetJobTree)
            .Produces<JobTreeResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/pages/{pageId:long}/links", GetPageLinks)
            .Produces<PageLinksResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapPost("/{id:guid}/cancel", CancelJob)
            .Produces<CancelJobResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> CreateJob(
        CreateJobRequest request, IJobService service, HttpContext http, CancellationToken ct)
    {
        var errors = JobRequestValidator.Validate(request, out var validated);
        if (validated is null)
            return Results.ValidationProblem(errors);

        var result = await service.CreateAsync(validated, http.TraceIdentifier, ct);

        // Both outcomes created the job; a deferred enqueue is retried by the reconciler within a minute.
        return Results.Created($"/api/jobs/{result.JobId}", new CreateJobResponse(result.JobId));
    }

    private static async Task<IResult> ListJobs(
        IJobService service, CancellationToken ct, int page = 1, int pageSize = JobLimits.DefaultPageSize)
    {
        var errors = JobRequestValidator.ValidatePaging(page, pageSize);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        return Results.Ok(await service.ListAsync(page, pageSize, ct));
    }

    private static async Task<IResult> GetJob(Guid id, IJobService service, CancellationToken ct)
    {
        var job = await service.GetAsync(id, ct);
        return job is null ? JobNotFound(id) : Results.Ok(job);
    }

    private static async Task<IResult> StreamJobEvents(
        Guid id, IJobService service, JobEventStream stream, HttpContext http)
    {
        var found = await stream.RunAsync(http, id, service);
        return found ? Results.Empty : JobNotFound(id);
    }

    private static async Task<IResult> GetJobTree(Guid id, IJobService service, CancellationToken ct)
    {
        var tree = await service.GetTreeAsync(id, ct);
        return tree is null ? JobNotFound(id) : Results.Ok(tree);
    }

    private static async Task<IResult> GetPageLinks(Guid id, long pageId, IJobService service, CancellationToken ct)
    {
        var links = await service.GetPageLinksAsync(id, pageId, ct);
        return links is null
            ? Results.Problem(
                title: "Page not found",
                detail: $"No page {pageId} exists in crawl job {id}.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(links);
    }

    private static async Task<IResult> CancelJob(Guid id, IJobService service, CancellationToken ct)
    {
        var result = await service.CancelAsync(id, ct);

        return result.Outcome switch
        {
            CancelJobOutcome.Canceled => Results.Accepted($"/api/jobs/{id}", result.Job),
            CancelJobOutcome.AlreadyTerminal => Results.Problem(
                title: "Job already finished",
                detail: $"Job {id} is already in a terminal state and cannot be canceled.",
                statusCode: StatusCodes.Status409Conflict),
            _ => JobNotFound(id)
        };
    }

    private static IResult JobNotFound(Guid id) => Results.Problem(
        title: "Job not found",
        detail: $"No crawl job with id {id} exists.",
        statusCode: StatusCodes.Status404NotFound);
}

using Crawler.Domain.Entities;

namespace Crawler.Api.Contracts;

/// <summary>Body of <c>POST /api/jobs</c>. Optional limits fall back to <see cref="JobLimits"/> defaults.</summary>
public sealed record CreateJobRequest(string? Url, int? MaxDepth, int? MaxPages);

public sealed record CreateJobResponse(Guid JobId);

public sealed record JobSummary(
    Guid JobId,
    string InputUrl,
    JobStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int PagesCrawled,
    int PagesDiscovered,
    int MaxDepth);

public sealed record JobDetails(
    Guid JobId,
    string InputUrl,
    string StartingDomain,
    int MaxDepth,
    int MaxPages,
    JobStatus Status,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int PagesCrawled,
    int PagesDiscovered,
    int ProgressPercent,
    long? DurationMs);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record CancelJobResponse(Guid JobId, JobStatus Status);

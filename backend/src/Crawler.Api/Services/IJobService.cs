using Crawler.Api.Contracts;

namespace Crawler.Api.Services;

/// <summary>Enqueued = message confirmed by the broker; EnqueueDeferred = job saved, publish failed, reconciler will retry.</summary>
public enum CreateJobOutcome { Enqueued, EnqueueDeferred }

public sealed record CreateJobResult(Guid JobId, CreateJobOutcome Outcome);

public enum CancelJobOutcome { Canceled, NotFound, AlreadyTerminal }

public sealed record CancelJobResult(CancelJobOutcome Outcome, CancelJobResponse? Job);

public interface IJobService
{
    Task<CreateJobResult> CreateAsync(ValidatedJobRequest request, string correlationId, CancellationToken ct);
    Task<PagedResult<JobSummary>> ListAsync(int page, int pageSize, CancellationToken ct);
    Task<JobDetails?> GetAsync(Guid jobId, CancellationToken ct);
    Task<JobTreeResponse?> GetTreeAsync(Guid jobId, CancellationToken ct);
    Task<PageLinksResponse?> GetPageLinksAsync(Guid jobId, long pageId, CancellationToken ct);
    Task<CancelJobResult> CancelAsync(Guid jobId, CancellationToken ct);
}

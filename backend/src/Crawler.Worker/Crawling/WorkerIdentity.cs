namespace Crawler.Worker.Crawling;

/// <summary>Identifies this worker process as a job lease owner. One instance per process (singleton).</summary>
public sealed class WorkerIdentity
{
    public string Id { get; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";
}

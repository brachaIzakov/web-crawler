using Crawler.Api.Services;
using Crawler.Domain.Entities;
using FluentAssertions;

namespace Crawler.Api.Tests;

public class JobProgressTests
{
    [Theory]
    // status, crawled, discovered, maxPages, expected
    [InlineData(JobStatus.Pending, 0, 0, 200, 0)]
    [InlineData(JobStatus.Pending, 5, 10, 200, 0)]
    [InlineData(JobStatus.Completed, 0, 0, 200, 100)]
    [InlineData(JobStatus.Completed, 3, 10, 200, 100)]
    [InlineData(JobStatus.Running, 0, 0, 200, 0)]
    [InlineData(JobStatus.Running, 0, 1, 200, 0)]
    [InlineData(JobStatus.Running, 10, 40, 200, 25)]
    [InlineData(JobStatus.Running, 1, 3, 200, 33)]
    [InlineData(JobStatus.Running, 2, 3, 200, 66)]
    [InlineData(JobStatus.Running, 5, 5, 200, 99)]
    [InlineData(JobStatus.Running, 50, 500, 100, 50)]
    [InlineData(JobStatus.Running, 100, 500, 100, 99)]
    [InlineData(JobStatus.Failed, 1, 1, 200, 100)]
    [InlineData(JobStatus.Failed, 0, 0, 200, 0)]
    [InlineData(JobStatus.Failed, 3, 12, 200, 25)]
    [InlineData(JobStatus.Canceled, 5, 10, 200, 50)]
    [InlineData(JobStatus.Canceled, 20, 10, 200, 100)]
    public void Percent_TableDriven(JobStatus status, int crawled, int discovered, int maxPages, int expected)
        => JobProgress.Percent(status, crawled, discovered, maxPages).Should().Be(expected);

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Canceled)]
    public void Percent_ZeroMaxPages_DoesNotDivideByZero(JobStatus status)
    {
        var act = () => JobProgress.Percent(status, 1, 1, 0);
        act.Should().NotThrow().Which.Should().Be(status == JobStatus.Running ? 99 : 100);
    }

    [Fact]
    public void DurationMs_NotStarted_IsNull()
        => JobProgress.DurationMs(null, null, DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void DurationMs_Completed_IsDifference()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        JobProgress.DurationMs(start, start.AddSeconds(2.5), start.AddHours(1)).Should().Be(2500, "completedAt wins over now");
    }

    [Fact]
    public void DurationMs_StillRunning_MeasuresUntilNow()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        JobProgress.DurationMs(start, null, start.AddSeconds(3)).Should().Be(3000);
    }
}

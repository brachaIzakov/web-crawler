using Crawler.Api.Hosting;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Crawler.Api.Tests;

/// <summary>Drives <see cref="JobReconciler.ReconcileOnceAsync"/> directly against the factory's SQLite database.</summary>
public sealed class JobReconcilerTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory;
    private readonly IJobPublisher _publisher = Substitute.For<IJobPublisher>();
    private readonly MutableTime _time = new(Now);
    private readonly JobReconciler _reconciler;

    public JobReconcilerTests(ApiFactory factory)
    {
        _factory = factory;
        _reconciler = new JobReconciler(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _publisher,
            _time,
            NullLogger<JobReconciler>.Instance);
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task SeedAsync(params CrawlJob[] jobs)
        => _factory.WithDbAsync(db => { db.Jobs.AddRange(jobs); return db.SaveChangesAsync(); });

    private Task<CrawlJob> ReloadAsync(Guid id)
        => _factory.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    [Fact]
    public async Task Reconcile_StalePendingJobOvertakenByYoungerStartedJob_IsRepublished()
    {
        var stale = Seed.Job(JobStatus.Pending, "https://example.com/s", createdAt: Now - JobReconciler.PendingGracePeriod - TimeSpan.FromSeconds(1), maxDepth: 3, maxPages: 40);
        await SeedAsync(stale, Overtaker(Now - TimeSpan.FromSeconds(30)));

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<CrawlJobRequested>(m => m.JobId == stale.Id && m.Url == "https://example.com/s" && m.MaxDepth == 3 && m.MaxPages == 40),
            $"reconciler:{stale.Id:N}",
            Arg.Any<CancellationToken>());
        (await ReloadAsync(stale.Id)).Status.Should().Be(JobStatus.Pending, "the worker moves it to Running");
    }

    [Fact]
    public async Task Reconcile_FreshPendingJob_IsNotRepublished()
    {
        await SeedAsync(
            Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromSeconds(10)),
            Overtaker(Now - TimeSpan.FromSeconds(5)));

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Canceled)]
    public async Task Reconcile_OldNonPendingJob_IsNotRepublished(JobStatus status)
    {
        await SeedAsync(Seed.Job(status, createdAt: Now - TimeSpan.FromHours(1)));

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Reconcile_StalePendingJobs_RepublishedOldestFirst()
    {
        var older = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(10));
        var newer = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(5));
        await SeedAsync(newer, older, Overtaker(Now - TimeSpan.FromMinutes(1)));
        var order = new List<Guid>();
        _publisher.PublishAsync(Arg.Do<CrawlJobRequested>(m => order.Add(m.JobId)), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        order.Should().Equal(older.Id, newer.Id);
    }

    [Fact]
    public async Task Reconcile_BrokerStillDown_StopsAfterFirstFailureWithoutThrowing()
    {
        await SeedAsync(
            Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(10)),
            Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(5)),
            Overtaker(Now - TimeSpan.FromMinutes(1)));
        _publisher.PublishAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("down"));

        var act = () => _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _publisher.ReceivedWithAnyArgs(1).PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Reconcile_StalePendingJobOvertakenWhileAnotherWorkerHoldsLiveLease_IsRepublished()
    {
        var lost = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(10));
        var busy = Overtaker(Now - TimeSpan.FromMinutes(8), JobStatus.Running);
        busy.LeaseOwner = "worker-1";
        busy.LeaseExpiresAt = Now + TimeSpan.FromMinutes(2);
        await SeedAsync(lost, busy);

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<CrawlJobRequested>(m => m.JobId == lost.Id), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_StalePendingJobNotOvertaken_IsNotRepublishedEvenWithLiveLeaseElsewhere()
    {
        // Still queued behind a long crawl that started before it was created: not lost, just waiting.
        var busy = Seed.Job(JobStatus.Running, createdAt: Now - TimeSpan.FromHours(1), startedAt: Now - TimeSpan.FromHours(1));
        busy.LeaseOwner = "worker-1";
        busy.LeaseExpiresAt = Now + TimeSpan.FromMinutes(2);
        var queued = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(30));
        var youngerQueued = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(20));
        await SeedAsync(busy, queued, youngerQueued);

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Reconcile_StalePendingJobNoOtherJobs_IsNotRepublished()
    {
        await SeedAsync(Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromHours(1)));

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Reconcile_RepublishedJob_BackedOffThenRepublishedAfterBackoff()
    {
        var lost = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(10));
        await SeedAsync(lost, Overtaker(Now - TimeSpan.FromMinutes(5)));

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);
        _time.UtcNow = Now + JobReconciler.Interval;
        await _reconciler.ReconcileOnceAsync(CancellationToken.None);
        await _publisher.ReceivedWithAnyArgs(1).PublishAsync(default!, default!, default);

        _time.UtcNow = Now + JobReconciler.RepublishBackoff;
        await _reconciler.ReconcileOnceAsync(CancellationToken.None);
        await _publisher.ReceivedWithAnyArgs(2).PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Reconcile_BackoffEntriesOlderThanBackoff_ArePruned()
    {
        var lost = Seed.Job(JobStatus.Pending, createdAt: Now - TimeSpan.FromMinutes(10));
        await SeedAsync(lost, Overtaker(Now - TimeSpan.FromMinutes(5)));
        await _reconciler.ReconcileOnceAsync(CancellationToken.None);
        _reconciler.RepublishBackoffCount.Should().Be(1);

        // The job gets picked up; its backoff entry must not outlive the backoff window.
        await _factory.WithDbAsync(db => db.Jobs.Where(j => j.Id == lost.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Completed)));
        _time.UtcNow = Now + JobReconciler.RepublishBackoff;
        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        _reconciler.RepublishBackoffCount.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_RunningJobWithLongExpiredLease_IsMarkedFailed()
    {
        var lost = Seed.Job(JobStatus.Running, startedAt: Now - TimeSpan.FromHours(1));
        lost.LeaseOwner = "worker-1";
        lost.LeaseExpiresAt = Now - JobReconciler.LostLeaseGracePeriod - TimeSpan.FromSeconds(1);
        await SeedAsync(lost);

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        var saved = await ReloadAsync(lost.Id);
        saved.Status.Should().Be(JobStatus.Failed);
        saved.FailureReason.Should().Contain("lease expired");
        saved.CompletedAt.Should().Be(Now);
        saved.LeaseOwner.Should().BeNull();
        saved.LeaseExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_RunningJobWithinGraceOrWithoutLease_IsLeftRunning()
    {
        var recentlyExpired = Seed.Job(JobStatus.Running);
        recentlyExpired.LeaseOwner = "worker-1";
        recentlyExpired.LeaseExpiresAt = Now - TimeSpan.FromMinutes(1);
        var live = Seed.Job(JobStatus.Running);
        live.LeaseExpiresAt = Now + TimeSpan.FromMinutes(1);
        var noLease = Seed.Job(JobStatus.Running);
        await SeedAsync(recentlyExpired, live, noLease);

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        (await ReloadAsync(recentlyExpired.Id)).Status.Should().Be(JobStatus.Running);
        (await ReloadAsync(live.Id)).Status.Should().Be(JobStatus.Running);
        (await ReloadAsync(noLease.Id)).Status.Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task Reconcile_TerminalJobWithStaleLease_IsUntouched()
    {
        var done = Seed.Job(JobStatus.Completed, completedAt: Now - TimeSpan.FromHours(2));
        done.LeaseExpiresAt = Now - TimeSpan.FromHours(1);
        await SeedAsync(done);

        await _reconciler.ReconcileOnceAsync(CancellationToken.None);

        var saved = await ReloadAsync(done.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.FailureReason.Should().BeNull();
    }

    /// <summary>A younger job that has already started: proof the queue moved past everything created before it.</summary>
    private static CrawlJob Overtaker(DateTime createdAt, JobStatus status = JobStatus.Completed)
        => Seed.Job(status, createdAt: createdAt, startedAt: createdAt + TimeSpan.FromSeconds(1),
            completedAt: status == JobStatus.Completed ? createdAt + TimeSpan.FromSeconds(30) : null);

    private sealed class MutableTime(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }
}

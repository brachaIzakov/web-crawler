using System.Collections.Concurrent;
using System.Diagnostics;
using Crawler.Domain.Entities;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests;

public sealed class RabbitMqJobProgressNotifierTests
{
    private static readonly Guid JobId = Guid.NewGuid();

    [Fact]
    public async Task NotifyAsync_PublishIsSlow_ReturnsImmediatelyAndDeliversNewestEventsInOrder()
    {
        using var transport = new BlockingTransport();
        using var notifier = NewNotifier(transport);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i <= 200; i++)
        {
            var task = notifier.NotifyAsync(Event(i), CancellationToken.None);
            task.IsCompletedSuccessfully.Should().BeTrue("enqueueing never waits for the broker");
        }
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        transport.Published.Should().BeEmpty("the first publish is still stuck");

        transport.Release();
        await WaitUntilAsync(() => transport.Published.LastOrDefault()?.PagesCrawled == 200);

        var crawled = transport.Published.Select(e => e.PagesCrawled).ToList();
        crawled.Should().BeInAscendingOrder();
        crawled.Count.Should().BeLessOrEqualTo(1 + RabbitMqJobProgressNotifier.QueueCapacity, "the queue is bounded (DropOldest)");
        crawled.Last().Should().Be(200, "the newest event (e.g. the terminal status) is never the one dropped");
    }

    [Fact]
    public void NotifyAsync_PublishFails_NeverThrowsAndSuppressesReconnectsDuringCooldown()
    {
        var transport = new FailingTransport();
        var notifier = NewNotifier(transport);

        for (var i = 1; i <= 5; i++)
            notifier.NotifyAsync(Event(i), CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue();
        notifier.Dispose();

        transport.Attempts.Should().Be(1, "after a failure, events are dropped until the cooldown ends");
        transport.Resets.Should().Be(1);
        transport.Disposed.Should().BeTrue("the queue was flushed, so the connection is released");
    }

    [Fact]
    public async Task Dispose_FlushesQueuedEvents()
    {
        var transport = new BlockingTransport();
        var notifier = NewNotifier(transport);
        await notifier.NotifyAsync(Event(1), CancellationToken.None);
        await notifier.NotifyAsync(Event(2, JobStatus.Completed), CancellationToken.None);

        transport.Release();
        notifier.Dispose();

        transport.Published.Select(e => e.Status).Should().EndWith(JobStatus.Completed);
        transport.Disposed.Should().BeTrue();
    }

    private static RabbitMqJobProgressNotifier NewNotifier(IProgressEventTransport transport)
        => new(transport, TimeProvider.System, NullLogger<RabbitMqJobProgressNotifier>.Instance);

    private static JobProgressChanged Event(int crawled, JobStatus status = JobStatus.Running)
        => new(JobId, status, crawled, crawled, DateTime.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5s");
            await Task.Delay(10);
        }
    }

    /// <summary>Every publish waits until <see cref="Release"/> — a broker connect that hangs.</summary>
    private sealed class BlockingTransport : IProgressEventTransport
    {
        private readonly ManualResetEventSlim _gate = new(false);
        public ConcurrentQueue<JobProgressChanged> Published { get; } = new();
        public bool Disposed { get; private set; }

        public void Release() => _gate.Set();

        public void Publish(JobProgressChanged evt)
        {
            _gate.Wait(TimeSpan.FromSeconds(10));
            Published.Enqueue(evt);
        }

        public void Reset() { }

        public void Dispose()
        {
            Disposed = true;
            _gate.Set();
        }
    }

    private sealed class FailingTransport : IProgressEventTransport
    {
        public int Attempts { get; private set; }
        public int Resets { get; private set; }
        public bool Disposed { get; private set; }

        public void Publish(JobProgressChanged evt)
        {
            Attempts++;
            throw new InvalidOperationException("broker unreachable");
        }

        public void Reset() => Resets++;
        public void Dispose() => Disposed = true;
    }
}

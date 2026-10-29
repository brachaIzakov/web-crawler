using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using FluentAssertions;

namespace Crawler.Worker.Tests;

public class DeliveryFailurePolicyTests
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(0, 1, 5)]
    [InlineData(1, 2, 10)]
    [InlineData(2, 3, 20)]
    public void Transient_WithRetriesLeft_RetriesWithExponentialBackoff(int retryCount, int expectedNext, int expectedDelaySeconds)
    {
        var decision = DeliveryFailurePolicy.Decide(new TimeoutException(), retryCount, MaxRetries, BaseDelay);

        decision.Action.Should().Be(DeliveryFailureAction.Retry);
        decision.NextRetryCount.Should().Be(expectedNext);
        decision.Delay.Should().Be(TimeSpan.FromSeconds(expectedDelaySeconds));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void Transient_RetriesExhausted_GoesToDlq(int retryCount)
    {
        var decision = DeliveryFailurePolicy.Decide(new HttpRequestException("down"), retryCount, MaxRetries, BaseDelay);

        decision.Action.Should().Be(DeliveryFailureAction.DeadLetterRetriesExhausted);
    }

    [Fact]
    public void NonTransient_GoesToDlqOnFirstAttempt()
    {
        var decision = DeliveryFailurePolicy.Decide(new JobNotFoundException(Guid.NewGuid()), 0, MaxRetries, BaseDelay);

        decision.Action.Should().Be(DeliveryFailureAction.DeadLetterNonTransient);
    }

    [Fact]
    public void ZeroMaxRetries_TransientGoesStraightToDlq()
    {
        var decision = DeliveryFailurePolicy.Decide(new TimeoutException(), 0, maxRetries: 0, BaseDelay);

        decision.Action.Should().Be(DeliveryFailureAction.DeadLetterRetriesExhausted);
    }

    [Fact]
    public void TransientInnerException_IsRetried()
    {
        var wrapped = new InvalidOperationException("outer", new TimeoutException());

        DeliveryFailurePolicy.Decide(wrapped, 0, MaxRetries, BaseDelay).Action.Should().Be(DeliveryFailureAction.Retry);
    }
}

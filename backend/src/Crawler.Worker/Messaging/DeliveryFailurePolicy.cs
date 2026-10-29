namespace Crawler.Worker.Messaging;

/// <summary>What the consumer does with a delivery whose processing threw.</summary>
public enum DeliveryFailureAction
{
    /// <summary>Publish a delayed copy to the retry queue and ack the original.</summary>
    Retry,
    /// <summary>Non-transient failure: reject without requeue so the broker routes it to the DLQ.</summary>
    DeadLetterNonTransient,
    /// <summary>Transient failure but the retry budget is spent: reject without requeue (DLQ).</summary>
    DeadLetterRetriesExhausted
}

/// <param name="Action">Retry or dead-letter.</param>
/// <param name="NextRetryCount">Value for <c>x-retry-count</c> on the retried copy (only meaningful for <see cref="DeliveryFailureAction.Retry"/>).</param>
/// <param name="Delay">Per-message TTL of the retried copy (only meaningful for <see cref="DeliveryFailureAction.Retry"/>).</param>
public readonly record struct DeliveryFailureDecision(DeliveryFailureAction Action, int NextRetryCount, TimeSpan Delay);

/// <summary>
/// Pure retry/DLQ decision for a failed delivery, kept apart from the RabbitMQ plumbing so the policy is unit-testable:
/// non-transient → DLQ; transient with <c>retryCount &lt; maxRetries</c> → retry after <c>baseDelay · 2^retryCount</c>;
/// otherwise → DLQ.
/// </summary>
public static class DeliveryFailurePolicy
{
    public static DeliveryFailureDecision Decide(Exception exception, int retryCount, int maxRetries, TimeSpan baseDelay)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!TransientFailureClassifier.IsTransient(exception))
            return new DeliveryFailureDecision(DeliveryFailureAction.DeadLetterNonTransient, retryCount, TimeSpan.Zero);

        if (retryCount >= maxRetries)
            return new DeliveryFailureDecision(DeliveryFailureAction.DeadLetterRetriesExhausted, retryCount, TimeSpan.Zero);

        var attempt = Math.Max(0, retryCount);
        var delay = TimeSpan.FromSeconds(baseDelay.TotalSeconds * Math.Pow(2, attempt));
        return new DeliveryFailureDecision(DeliveryFailureAction.Retry, attempt + 1, delay);
    }
}

using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crawler.Worker.Messaging;

/// <summary>
/// Decides whether a failed message is worth retrying. Transient = infrastructure that may recover
/// (database, broker, network, timeouts). Anything else (bad data, missing job, bugs) goes straight to the DLQ.
/// </summary>
public static class TransientFailureClassifier
{
    private const string RabbitMqExceptionNamespace = "RabbitMQ.Client.Exceptions";

    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Flatten(exception).Any(IsTransientType);
    }

    private static bool IsTransientType(Exception ex) => ex switch
    {
        // NpgsqlException overrides DbException.IsTransient (connection loss, timeouts, serialization failures...).
        DbException db => db.IsTransient,
        // EF Core's execution strategy gave up after its own retries; a later redelivery may still succeed.
        RetryLimitExceededException => true,
        TimeoutException or HttpRequestException or SocketException => true,
        // Not caused by worker shutdown (that case is handled by the consumer), e.g. a command timeout.
        OperationCanceledException => true,
        _ => ex.GetType().Namespace == RabbitMqExceptionNamespace
    };

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}

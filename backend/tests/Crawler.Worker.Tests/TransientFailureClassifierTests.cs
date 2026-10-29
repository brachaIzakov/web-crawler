using System.Net.Sockets;
using System.Text.Json;
using Crawler.Worker.Crawling;
using Crawler.Worker.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Crawler.Worker.Tests;

public class TransientFailureClassifierTests
{
    private static AlreadyClosedException BrokerClosed()
        => new(new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "CONNECTION_FORCED"));

    public static TheoryData<Exception> TransientExceptions => new()
    {
        new TimeoutException(),
        new HttpRequestException("connection refused"),
        new SocketException((int)SocketError.ConnectionRefused),
        BrokerClosed(),
        new BrokerUnreachableException(new IOException("down")),
        new OperationCanceledException(),
        new TaskCanceledException(),
        new DbUpdateException("save failed", new TimeoutException()),
        new RetryLimitExceededException("gave up"),
        new FakeDbException(isTransient: true),
    };

    public static TheoryData<Exception> PermanentExceptions => new()
    {
        new ArgumentException("bad"),
        new JobNotFoundException(Guid.NewGuid()),
        new InvalidOperationException("bug"),
        new JsonException("malformed"),
        new DbUpdateException("constraint violated"),
        new DbUpdateException("constraint violated", new FakeDbException(isTransient: false)),
        new FakeDbException(isTransient: false),
        new NullReferenceException(),
    };

    [Theory]
    [MemberData(nameof(TransientExceptions))]
    public void IsTransient_InfrastructureFailures_AreTransient(Exception ex)
        => TransientFailureClassifier.IsTransient(ex).Should().BeTrue();

    [Theory]
    [MemberData(nameof(PermanentExceptions))]
    public void IsTransient_DataOrCodeFailures_AreNotTransient(Exception ex)
        => TransientFailureClassifier.IsTransient(ex).Should().BeFalse();

    [Fact]
    public void IsTransient_DeeplyNestedTransientInner_IsDetected()
    {
        var ex = new InvalidOperationException("outer",
            new ArgumentException("middle",
                new DbUpdateException("save", new SocketException())));

        TransientFailureClassifier.IsTransient(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_NestedOnlyPermanentInners_IsNotTransient()
    {
        var ex = new InvalidOperationException("outer", new ArgumentException("inner", new FormatException()));
        TransientFailureClassifier.IsTransient(ex).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_AggregateWithOneTransientInner_IsTransient()
    {
        var ex = new AggregateException(new ArgumentException(), new InvalidOperationException("x", new TimeoutException()));
        TransientFailureClassifier.IsTransient(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_AggregateWithOnlyPermanentInners_IsNotTransient()
    {
        var ex = new AggregateException(new ArgumentException(), new JsonException());
        TransientFailureClassifier.IsTransient(ex).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_Null_Throws()
    {
        var act = () => TransientFailureClassifier.IsTransient(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class FakeDbException(bool isTransient) : System.Data.Common.DbException("db")
    {
        public override bool IsTransient { get; } = isTransient;
    }
}

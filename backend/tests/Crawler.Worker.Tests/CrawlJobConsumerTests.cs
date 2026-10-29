using Crawler.Worker.Messaging;
using FluentAssertions;
using NSubstitute;
using RabbitMQ.Client;

namespace Crawler.Worker.Tests;

public sealed class CrawlJobConsumerTests
{
    [Theory]
    [InlineData(true, false, true)]   // channel closed on a live connection (e.g. confirm timeout): never recovered
    [InlineData(true, true, false)]   // healthy
    [InlineData(false, false, false)] // connection lost: automatic recovery reopens both
    public void NeedsRebuild_OnlyForClosedChannelOnOpenConnection(bool connectionOpen, bool channelOpen, bool expected)
    {
        var connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(connectionOpen);
        var channel = Substitute.For<IModel>();
        channel.IsOpen.Returns(channelOpen);

        CrawlJobConsumer.NeedsRebuild(connection, channel).Should().Be(expected);
    }

    [Fact]
    public void NeedsRebuild_NotYetConnected_IsFalse()
        => CrawlJobConsumer.NeedsRebuild(null, null).Should().BeFalse();
}

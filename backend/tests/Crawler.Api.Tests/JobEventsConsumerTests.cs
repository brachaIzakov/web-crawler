using Crawler.Api.Streaming;
using FluentAssertions;

namespace Crawler.Api.Tests;

/// <summary>The watchdog's rebuild decision; the rebuild itself needs a live broker and is covered by the docker e2e run.</summary>
public class JobEventsConsumerTests
{
    [Theory]
    [InlineData(true, false, true)]   // channel closed on a live connection: recovery will not reopen it
    [InlineData(true, true, false)]   // healthy
    [InlineData(false, false, false)] // connection down: automatic recovery owns it
    [InlineData(false, true, false)]
    public void NeedsRebuild_OnlyWhenChannelClosedOnOpenConnection(bool connectionOpen, bool channelOpen, bool expected)
        => JobEventsConsumer.NeedsRebuild(connectionOpen, channelOpen).Should().Be(expected);
}

using Crawler.Worker.Crawling;
using FluentAssertions;

namespace Crawler.Worker.Tests;

public sealed class TextLimitsTests
{
    private const string Emoji = "\U0001F600"; // one surrogate pair
    private const char Replacement = (char)0xFFFD;

    [Fact]
    public void Truncate_CutInsideSurrogatePair_DropsTheWholePair()
    {
        var value = new string('a', 4) + Emoji + "b";

        TextLimits.Truncate(value, 5).Should().Be("aaaa");
    }

    [Fact]
    public void Truncate_CutAfterSurrogatePair_KeepsIt()
        => TextLimits.Truncate("aaaa" + Emoji + "b", 6).Should().Be("aaaa" + Emoji);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Truncate_ShortOrNull_IsUnchanged(string? value)
        => TextLimits.Truncate(value, 10).Should().Be(value);

    [Fact]
    public void Sanitize_RemovesNulAndReplacesLoneSurrogates_KeepsValidPairs()
    {
        var value = "a\0b" + "\uD83D" + "c" + "\uDE00" + Emoji;

        TextLimits.Sanitize(value).Should().Be($"ab{Replacement}c{Replacement}{Emoji}");
    }

    [Fact]
    public void Sanitize_CleanText_ReturnsSameInstance()
    {
        const string value = "Plain title";
        TextLimits.Sanitize(value).Should().BeSameAs(value);
    }
}

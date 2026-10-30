using static MusicPlayer.Library.Tests.TestLyrics;

namespace MusicPlayer.Library.Tests;

public class LrcWriterTests
{
    [Theory]
    [InlineData(0, "[00:00.00]")]
    [InlineData(83_450, "[01:23.45]")]
    [InlineData(1_004, "[00:01.00]")]
    [InlineData(1_006, "[00:01.01]")]
    [InlineData(59_996, "[01:00.00]")]
    [InlineData(6_000_000, "[100:00.00]")]
    [InlineData(-500, "[00:00.00]")]
    public void Stamp_RoundsToHundredths(int ms, string expected) =>
        Assert.Equal(expected, LrcWriter.Stamp(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Format_Synced_WritesStampsAndExtraLines()
    {
        var lyrics = Synced(
            new LyricLine(At(1.5), "Hola") { Extra = ["Hello"] },
            Line(10, ""),
            Line(83.45, "Adiós"));

        Assert.Equal("[00:01.50]Hola\n[00:01.50]Hello\n[00:10.00]\n[01:23.45]Adiós", LrcWriter.Format(lyrics));
    }

    [Fact]
    public void Format_Synced_RoundTripsThroughParser()
    {
        var original = Synced(
            new LyricLine(At(1.5), "Hola") { Extra = ["Hello"] },
            Line(10, ""),
            Line(12.34, "[Chorus]"),
            new LyricLine(At(20), "Uno") { Extra = ["One", "Satu"] },
            Line(83.45, "Adiós"),
            Line(6000, "Long song"));

        AssertSameLines(original, LrcParser.Parse(LrcWriter.Format(original)));
    }

    [Fact]
    public void Format_Plain_WritesTextOnly()
    {
        var lyrics = Plain("First verse", "goes here", "", "[Chorus]", "la la la");

        Assert.Equal("First verse\ngoes here\n\n[Chorus]\nla la la", LrcWriter.Format(lyrics));
    }

    [Fact]
    public void Format_Plain_RoundTripsThroughParser()
    {
        var original = Plain("First verse", "goes here", "", "[Chorus]", "la la la");

        AssertSameLines(original, LrcParser.Parse(LrcWriter.Format(original)));
    }

    private static void AssertSameLines(Lyrics expected, Lyrics? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.IsSynced, actual.IsSynced);
        Assert.Equal(Times(expected), Times(actual));
        Assert.Equal(Texts(expected), Texts(actual));
        for (int i = 0; i < expected.Lines.Count; i++)
            Assert.Equal(expected.Lines[i].Extra, actual.Lines[i].Extra);
    }
}

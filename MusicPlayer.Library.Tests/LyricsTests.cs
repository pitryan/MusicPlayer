using static MusicPlayer.Library.Tests.TestLyrics;

namespace MusicPlayer.Library.Tests;

public class LyricsTests
{
    private static readonly Lyrics ThreeLines = Synced((1, "a"), (2, "b"), (3, "c"));

    private static readonly TimeSpan MinimumGap = At(5);

    [Theory]
    [InlineData(0.0, -1)]
    [InlineData(0.999, -1)]
    [InlineData(1.0, 0)]
    [InlineData(1.5, 0)]
    [InlineData(2.0, 1)]
    [InlineData(2.999, 1)]
    [InlineData(3.0, 2)]
    [InlineData(600.0, 2)]
    public void IndexAt_IsLastLineAlreadyStarted(double seconds, int expected) =>
        Assert.Equal(expected, ThreeLines.IndexAt(At(seconds)));

    [Fact]
    public void IndexAt_MatchesLinearSearchOnLongLyrics()
    {
        var lines = Enumerable.Range(0, 37).Select(i => Line(5 + i * 2.5, $"line {i}")).ToList();
        var lyrics = new Lyrics(lines, isSynced: true);

        for (int ms = 0; ms < 110_000; ms += 250)
        {
            var position = TimeSpan.FromMilliseconds(ms);
            Assert.Equal(lines.FindLastIndex(l => l.Time <= position), lyrics.IndexAt(position));
        }
    }

    [Fact]
    public void IndexAt_Unsynced_IsAlwaysMinusOne()
    {
        var lyrics = Plain("a", "b");

        Assert.Equal(-1, lyrics.IndexAt(TimeSpan.Zero));
        Assert.Equal(-1, lyrics.IndexAt(At(60)));
    }

    [Fact]
    public void IndexAt_NoLines_IsMinusOne() => Assert.Equal(-1, new Lyrics([], isSynced: true).IndexAt(At(5)));

    [Fact]
    public void WithInterludes_Unsynced_ReturnsLyricsUnchanged()
    {
        var lyrics = Plain("a", "", "b");

        Assert.Same(lyrics, lyrics.WithInterludes(MinimumGap));
    }

    [Fact]
    public void WithInterludes_ShortGap_IsDropped()
    {
        var result = Synced((1, "a"), (5, ""), (7, "b")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "a", "b" }, Texts(result));
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(10.0)] // tepat sepanjang minimumGap: tidak lebih pendek, jadi tetap ada
    public void WithInterludes_LongGap_IsKept(double nextLine)
    {
        var result = Synced((1, "a"), (5, ""), (nextLine, "b")).WithInterludes(MinimumGap);

        Assert.True(result.IsSynced);
        Assert.Equal(new[] { "a", "", "b" }, Texts(result));
        Assert.Equal(At(5), result.Lines[1].Time);
    }

    [Fact]
    public void WithInterludes_IntroBlankLine_StartsAtZero()
    {
        var result = Synced((3, ""), (20, "a")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "", "a" }, Texts(result));
        Assert.Equal(new[] { TimeSpan.Zero, At(20) }, Times(result));
    }

    [Theory]
    [InlineData(12.0)]
    [InlineData(5.0)]
    public void WithInterludes_LateFirstLine_GetsIntroInterlude(double firstLine)
    {
        var result = Synced((firstLine, "a"), (firstLine + 3, "b")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "", "a", "b" }, Texts(result));
        Assert.Equal(TimeSpan.Zero, result.Lines[0].Time);
    }

    [Fact]
    public void WithInterludes_ShortIntroBlankBeforeLateFirstLine_StillGetsIntro()
    {
        // Baris kosongnya hanya 4 detik sebelum baris pertama, tapi intronya 8 detik dari awal lagu
        var result = Synced((4, ""), (8, "a")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "", "a" }, Texts(result));
        Assert.Equal(new[] { TimeSpan.Zero, At(8) }, Times(result));
    }

    [Fact]
    public void WithInterludes_EarlyFirstLine_GetsNoIntro()
    {
        var result = Synced((0, ""), (3, "a"), (6, "b")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "a", "b" }, Texts(result));
    }

    [Fact]
    public void WithInterludes_TrailingBlanks_AreRemoved()
    {
        var result = Synced((1, "a"), (2, "b"), (30, ""), (60, "")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "a", "b" }, Texts(result));
    }

    [Fact]
    public void WithInterludes_ConsecutiveBlanks_KeepOnlyTheFirst()
    {
        var result = Synced((1, "a"), (5, ""), (8, ""), (30, "b")).WithInterludes(MinimumGap);

        Assert.Equal(new[] { "a", "", "b" }, Texts(result));
        Assert.Equal(At(5), result.Lines[1].Time);
    }

    [Fact]
    public void WithInterludes_KeepsLineLayers()
    {
        var line = new LyricLine(At(1), "一") { Extra = ["one"], Reading = "ichi", Translation = "satu" };

        var result = Synced(line, Line(2, "")).WithInterludes(MinimumGap);

        Assert.Same(line, Assert.Single(result.Lines));
    }
}

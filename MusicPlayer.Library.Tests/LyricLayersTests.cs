using static MusicPlayer.Library.Tests.TestLyrics;

namespace MusicPlayer.Library.Tests;

public class LyricLayersTests
{
    [Fact]
    public void SplitLines_NullOrEmpty_IsEmpty()
    {
        Assert.Empty(LyricLayers.SplitLines(null));
        Assert.Empty(LyricLayers.SplitLines(""));
    }

    [Fact]
    public void SplitLines_AnyLineEnding_KeepsBlankLines() =>
        Assert.Equal(new[] { "a", "", "b", "c", "" }, LyricLayers.SplitLines("a\r\n\nb\rc\n"));

    [Fact]
    public void JoinLines_OneLinePerValue()
    {
        string text = LyricLayers.JoinLines(new[] { " a ", null, "b\r\nc", "d", "", null });

        // Baris baru di dalam satu nilai jadi spasi, supaya baris sesudahnya tidak bergeser
        Assert.Equal("a\n\nb c\nd", text);
    }

    [Fact]
    public void JoinLines_AllBlank_IsEmpty() => Assert.Equal("", LyricLayers.JoinLines(new[] { null, "", "  " }));

    [Fact]
    public void JoinLines_SplitLines_RoundTrip()
    {
        var values = new[] { "ichi", "", "san" };

        Assert.Equal(values, LyricLayers.SplitLines(LyricLayers.JoinLines(values)));
    }

    [Fact]
    public void Attach_TagsLayersByLineOrder()
    {
        var lyrics = Synced((1, "一"), (2, "二"), (3, "三"));

        var result = LyricLayers.Attach(lyrics, "ichi\n  \nsan", " satu \ndua");

        Assert.Equal(new[] { "ichi", null, "san" }, Readings(result));
        Assert.Equal(new[] { "satu", "dua", null }, Translations(result));
        Assert.Equal(Texts(lyrics), Texts(result));
        Assert.Equal(Times(lyrics), Times(result));
        Assert.True(result.IsSynced);
    }

    [Fact]
    public void Attach_NoTags_ReturnsLyricsUnchanged()
    {
        var lyrics = Plain("a", "b");

        Assert.Same(lyrics, LyricLayers.Attach(lyrics, null, ""));
    }

    [Fact]
    public void Attach_MoreTagLinesThanLyrics_IgnoresTheRest()
    {
        var result = LyricLayers.Attach(Plain("a"), "r1\nr2\nr3", null);

        Assert.Equal("r1", Assert.Single(result.Lines).Reading);
        Assert.False(result.IsSynced);
    }

    [Fact]
    public void Attach_EmptyTagLine_KeepsParsedLayers()
    {
        // Lapisan dari LRC tiga lapis
        var lyrics = Synced(
            new LyricLine(At(1), "一") { Reading = "ichi", Translation = "satu" },
            new LyricLine(At(2), "二") { Reading = "ni", Translation = "dua" });

        var result = LyricLayers.Attach(lyrics, "\nNI", null);

        Assert.Equal(new[] { "ichi", "NI" }, Readings(result));
        Assert.Equal(new[] { "satu", "dua" }, Translations(result));
    }

    [Fact]
    public void Distribute_SkipsBlankLinesOnBothSides()
    {
        var (values, leftover, missing) = LyricLayers.Distribute(new[] { "a", "", "b", "  ", "c" }, "x\n\n  y  \n\nz\n");

        Assert.Equal(new[] { "x", null, "y", null, "z" }, values);
        Assert.Equal(0, leftover);
        Assert.Equal(0, missing);
    }

    [Fact]
    public void Distribute_MorePastedLines_CountsLeftover()
    {
        var (values, leftover, missing) = LyricLayers.Distribute(new[] { "a", "b" }, "x\ny\nz\nw");

        Assert.Equal(new[] { "x", "y" }, values);
        Assert.Equal(2, leftover);
        Assert.Equal(0, missing);
    }

    [Fact]
    public void Distribute_FewerPastedLines_CountsMissing()
    {
        var (values, leftover, missing) = LyricLayers.Distribute(new[] { "a", "", "b", "c" }, "x");

        Assert.Equal(new[] { "x", null, null, null }, values);
        Assert.Equal(0, leftover);
        Assert.Equal(2, missing);
    }

    [Fact]
    public void HasLayers_NoReadingOrTranslation_IsFalse()
    {
        Assert.False(LyricLayers.HasLayers(null));
        Assert.False(LyricLayers.HasLayers(Synced(new LyricLine(At(1), "a") { Extra = ["b"] })));
    }

    [Fact]
    public void HasLayers_AnyReadingOrTranslation_IsTrue()
    {
        Assert.True(LyricLayers.HasLayers(Tagged(("a", null, null), ("b", "r", null))));
        Assert.True(LyricLayers.HasLayers(Tagged(("a", null, "t"))));
    }

    [Fact]
    public void Transfer_SourceWithoutLayers_ReturnsTargetUnchanged()
    {
        var to = Synced((1, "a"));

        Assert.Same(to, LyricLayers.Transfer(Plain("a"), to));
    }

    [Fact]
    public void Transfer_ExactMatch_CopiesLayersAndKeepsTarget()
    {
        var from = Tagged(("一", "ichi", "satu"), ("", null, null), ("二", "ni", "dua"));
        var to = Synced(new LyricLine(At(1), "一") { Extra = ["one"] }, Line(2, "二"));

        var result = LyricLayers.Transfer(from, to);

        Assert.Equal(new[] { "ichi", "ni" }, Readings(result));
        Assert.Equal(new[] { "satu", "dua" }, Translations(result));
        Assert.Equal(Texts(to), Texts(result));
        Assert.Equal(Times(to), Times(result));
        Assert.Equal(new[] { "one" }, result.Lines[0].Extra);
        Assert.True(result.IsSynced);
    }

    [Theory]
    [InlineData("もうちょっとなのに", "もうちょっとなのに（なのに）")] // gema penyanyi latar ala LRCLIB
    [InlineData("I'm alive", "I'm alive (alive!)")]
    [InlineData("Hold on", "Hold on [hold on]")]
    [InlineData("ＨＥＬＬＯ　ｗｏｒｌｄ", "Hello world")]
    [InlineData("ｶﾀｶﾅ", "カタカナ")]
    [InlineData("Hello, World!", "hello world")]
    [InlineData("Ooh", "(Ooh)")] // seluruh baris di dalam kurung: isi kurungnya yang dibandingkan
    public void Transfer_MatchesLinesWrittenDifferently(string source, string target)
    {
        var result = LyricLayers.Transfer(Tagged((source, "reading", "translation")), Synced((1, target)));

        var line = Assert.Single(result.Lines);
        Assert.Equal("reading", line.Reading);
        Assert.Equal("translation", line.Translation);
        Assert.Equal(target, line.Text);
    }

    [Fact]
    public void Transfer_SlightlyLongerLine_StillMatches()
    {
        var result = LyricLayers.Transfer(
            Tagged(("I will love you so much", "r", null)), Synced((1, "I will love you so much, baby")));

        Assert.Equal("r", Assert.Single(result.Lines).Reading);
    }

    [Fact]
    public void Transfer_MuchLongerLine_DoesNotMatch()
    {
        var result = LyricLayers.Transfer(Tagged(("Love", "r", null)), Synced((1, "Love you forever and ever")));

        Assert.Null(Assert.Single(result.Lines).Reading);
    }

    [Fact]
    public void Transfer_TwoSourceLines_MergeIntoOneTarget()
    {
        var from = Tagged(("君の名前を", "Kimi no namae wo", "Namamu"), ("呼んでいた", "yonde ita", null));

        var result = LyricLayers.Transfer(from, Synced((1, "君の名前を 呼んでいた")));

        var line = Assert.Single(result.Lines);
        Assert.Equal("Kimi no namae wo yonde ita", line.Reading);
        Assert.Equal("Namamu", line.Translation);
    }

    [Fact]
    public void Transfer_ThreeSourceLines_MergeIntoOneTarget()
    {
        var from = Tagged(("Hold me", "r1", "t1"), ("close", "r2", "t2"), ("tonight", "r3", "t3"), ("Again", "r4", "t4"));

        var result = LyricLayers.Transfer(from, Synced((1, "Hold me close tonight"), (4, "Again")));

        Assert.Equal(new[] { "r1 r2 r3", "r4" }, Readings(result));
        Assert.Equal(new[] { "t1 t2 t3", "t4" }, Translations(result));
    }

    [Fact]
    public void Transfer_FourSourceLines_AreNotMerged()
    {
        var from = Tagged(("one", "r1", null), ("two", "r2", null), ("three", "r3", null), ("four", "r4", null),
            ("five", "r5", null));

        var result = LyricLayers.Transfer(from, Synced((1, "one two three four"), (4, "five")));

        Assert.Equal(new[] { null, "r5" }, Readings(result));
    }

    [Fact]
    public void Transfer_RepeatedChorus_PairsInOrder()
    {
        var from = Tagged(
            ("Verse one", null, "Bait satu"),
            ("Chorus line", null, "Reff pertama"),
            ("Verse two", null, "Bait dua"),
            ("Chorus line", null, "Reff kedua"));
        var to = Synced((1, "Verse one"), (5, "Chorus line"), (9, "Verse two"), (13, "Chorus line"));

        var result = LyricLayers.Transfer(from, to);

        Assert.Equal(new[] { "Bait satu", "Reff pertama", "Bait dua", "Reff kedua" }, Translations(result));
    }

    [Fact]
    public void Transfer_UnmatchedTargetLines_GetNothing()
    {
        var from = Tagged(
            ("Walking in the rain", "r1", "t1"),
            ("Chorus line", "r2", "t2"),
            ("Dancing in the sun", "r3", "t3"));
        // Reff kedua tidak ada di sumber, jadi tidak ikut mengambil lapisan reff pertama
        var to = Synced(
            (1, "Walking in the rain"), (3, "An ad-lib nobody wrote down"), (5, "Chorus line"),
            (7, "Dancing in the sun"), (9, "Chorus line"));

        var result = LyricLayers.Transfer(from, to);

        Assert.Equal(new[] { "r1", null, "r2", "r3", null }, Readings(result));
        Assert.Equal(new[] { "t1", null, "t2", "t3", null }, Translations(result));
    }

    [Fact]
    public void Transfer_BlankTargetLines_StayBlank()
    {
        var from = Tagged(("first line", "r1", null), ("second line", "r2", null));
        var to = Synced((0, ""), (5, "first line"), (10, ""), (20, "second line"));

        var result = LyricLayers.Transfer(from, to);

        Assert.Equal(new[] { null, "r1", null, "r2" }, Readings(result));
        Assert.Equal(Texts(to), Texts(result));
        Assert.Equal(Times(to), Times(result));
    }

    [Fact]
    public void Transfer_SourceWithoutLayersForALine_KeepsTargetLayers()
    {
        var from = Tagged(("一", "ichi", null), ("二", null, null));
        var to = Synced(
            new LyricLine(At(1), "一") { Translation = "satu" },
            new LyricLine(At(2), "二") { Reading = "ni", Translation = "dua" });

        var result = LyricLayers.Transfer(from, to);

        Assert.Equal(new[] { "ichi", "ni" }, Readings(result));
        Assert.Equal(new[] { "satu", "dua" }, Translations(result));
    }
}

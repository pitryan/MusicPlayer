using static MusicPlayer.Library.Tests.TestLyrics;

namespace MusicPlayer.Library.Tests;

public class LrcParserTests
{
    // Baris-baris digabung dengan "\n", supaya isi file LRC di setiap tes mudah dibaca
    private static Lyrics Parse(params string[] lines)
    {
        var lyrics = LrcParser.Parse(string.Join("\n", lines));
        Assert.NotNull(lyrics);
        return lyrics;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t\r\n ")]
    public void Parse_EmptyText_ReturnsNull(string? text) => Assert.Null(LrcParser.Parse(text));

    [Fact]
    public void Parse_OnlyInfoTags_ReturnsNull() =>
        Assert.Null(LrcParser.Parse("[ar:Artist]\n[ti:Title]\n[offset:+100]"));

    [Fact]
    public void Parse_TimedLines_IsSynced()
    {
        var lyrics = Parse("[00:01.00]First", "[00:02.50]Second");

        Assert.True(lyrics.IsSynced);
        Assert.Equal(new[] { "First", "Second" }, Texts(lyrics));
        Assert.Equal(new[] { At(1), At(2.5) }, Times(lyrics));
    }

    [Fact]
    public void Parse_TextWithoutTimes_IsPlain()
    {
        var lyrics = Parse("First line", "Second line");

        Assert.False(lyrics.IsSynced);
        Assert.Equal(new[] { "First line", "Second line" }, Texts(lyrics));
        Assert.All(lyrics.Lines, l => Assert.Equal(TimeSpan.Zero, l.Time));
    }

    [Fact]
    public void Parse_LrcWithAFewUntimedLines_IsSyncedAndDropsThem()
    {
        var lyrics = Parse("Song Title", "Lyrics by Someone", "[00:01.00]a", "[00:02.00]b", "[00:03.00]c");

        Assert.True(lyrics.IsSynced);
        Assert.Equal(new[] { "a", "b", "c" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_MostlyUntimedText_IsPlainAndKeepsEveryLine()
    {
        // Satu tanda waktu nyasar tidak membuat teks biasa dianggap LRC; teks barisnya tetap ikut
        var lyrics = Parse("a", "b", "[00:01.00]c", "d");

        Assert.False(lyrics.IsSynced);
        Assert.Equal(new[] { "a", "b", "c", "d" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_AnyLineEnding_SplitsLines()
    {
        var lyrics = LrcParser.Parse("[00:01.00]a\r\n[00:02.00]b\r[00:03.00]c\n[00:04.00]d");

        Assert.NotNull(lyrics);
        Assert.Equal(new[] { "a", "b", "c", "d" }, Texts(lyrics));
    }

    [Theory]
    [InlineData("[01:23]x", 83_000)]
    [InlineData("[01:23.4]x", 83_400)]
    [InlineData("[01:23.45]x", 83_450)]
    [InlineData("[01:23.456]x", 83_456)]
    [InlineData("[01:23.05]x", 83_050)]
    [InlineData("[01:23:45]x", 83_450)]
    [InlineData("[01:23,45]x", 83_450)]
    [InlineData("[1:02.5]x", 62_500)]
    [InlineData("[123:00.00]x", 7_380_000)]
    [InlineData("[ 00:07.00 ]x", 7_000)]
    public void Parse_TimeTagFormats(string text, int expectedMs)
    {
        var line = Assert.Single(Parse(text).Lines);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), line.Time);
        Assert.Equal("x", line.Text);
    }

    [Fact]
    public void Parse_SecondsOver59_IsNotATimeTag() => Assert.False(Parse("[00:75.00]x").IsSynced);

    [Fact]
    public void Parse_LinesOutOfOrder_AreSortedByTime()
    {
        var lyrics = Parse("[00:03.00]c", "[00:01.00]a", "[00:02.00]b");

        Assert.Equal(new[] { "a", "b", "c" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_SeveralLeadingTimes_RepeatTheLine()
    {
        var lyrics = Parse("[00:10.00][01:20.00]Chorus", "[00:20.00]Verse", "[00:40.00] [01:00.00]Bridge");

        Assert.Equal(new[] { "Chorus", "Verse", "Bridge", "Bridge", "Chorus" }, Texts(lyrics));
        Assert.Equal(new[] { At(10), At(20), At(40), At(60), At(80) }, Times(lyrics));
    }

    [Theory]
    [InlineData("[offset:+500]", 500, 1500)]
    [InlineData("[offset:500]", 500, 1500)]
    [InlineData("[offset:-500]", 1500, 2500)]
    [InlineData("[OFFSET: -500 ]", 1500, 2500)]
    public void Parse_Offset_ShiftsEveryLine(string offsetTag, int firstMs, int secondMs)
    {
        // Offset positif berarti lirik tampil lebih awal
        var lyrics = Parse(offsetTag, "[00:01.00]a", "[00:02.00]b");

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(firstMs), TimeSpan.FromMilliseconds(secondMs) }, Times(lyrics));
    }

    [Fact]
    public void Parse_OffsetBeforeSongStart_ClampsAtZero()
    {
        var lyrics = Parse("[offset:+2000]", "[00:01.00]a", "[00:05.00]b");

        Assert.Equal(new[] { TimeSpan.Zero, At(3) }, Times(lyrics));
        Assert.Equal(new[] { "a", "b" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_InfoTags_AreIgnored()
    {
        var lyrics = Parse(
            "[ar:Artist]", "[ti:Title]", "[al:Album]", "[au:Writer]", "[by:Someone]", "[re:Some Tool]",
            "[ve:1.0]", "[length:03:20]", "[#:comment]", "[00:01.00]a");

        Assert.True(lyrics.IsSynced);
        Assert.Equal(new[] { "a" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_SectionHeadersInPlainText_AreKept()
    {
        var lyrics = Parse("[Chorus]", "la la la", "[Intro: Drake]", "yeah");

        Assert.False(lyrics.IsSynced);
        Assert.Equal(new[] { "[Chorus]", "la la la", "[Intro: Drake]", "yeah" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_SectionHeaderAfterTime_IsKeptAsText()
    {
        var lyrics = Parse("[00:01.00][Chorus]", "[00:02.00]la la la");

        Assert.Equal(new[] { "[Chorus]", "la la la" }, Texts(lyrics));
    }

    [Theory]
    [InlineData("[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>", "Hello world")]
    [InlineData("[00:01.00]Hello <00:01.50> world", "Hello world")]
    [InlineData("[00:01.00]Hello [00:01.50]world", "Hello world")]
    [InlineData("[00:01.00]  Hello \t  world  ", "Hello world")]
    public void Parse_InlineWordTimes_AreStrippedFromText(string text, string expected)
    {
        var line = Assert.Single(Parse(text).Lines);

        Assert.Equal(expected, line.Text);
        Assert.Equal(At(1), line.Time);
    }

    [Fact]
    public void Parse_TimedBlankLine_IsKeptAsInterlude()
    {
        var lyrics = Parse("[00:01.00]a", "[00:10.00]", "[00:20.00]b");

        Assert.Equal(new[] { "a", "", "b" }, Texts(lyrics));
        Assert.True(lyrics.Lines[1].IsBlank);
        Assert.Equal(At(10), lyrics.Lines[1].Time);
    }

    [Fact]
    public void Parse_UntimedBlankLinesInLrc_AreDropped()
    {
        var lyrics = Parse("[00:01.00]a", "", "", "[00:02.00]b", "");

        Assert.Equal(new[] { "a", "b" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_PlainBlankLines_CollapseToOneStanzaBreak()
    {
        var lyrics = Parse("", "  ", "a", "b", "", "", "", "c", "", "");

        Assert.Equal(new[] { "a", "b", "", "c" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_ByteOrderMark_IsIgnoredInLrc()
    {
        var lyrics = Parse("﻿[ti:Title]", "[00:01.00]a", "[00:02.00]b");

        Assert.True(lyrics.IsSynced);
        Assert.Equal(new[] { "a", "b" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_ByteOrderMark_IsIgnoredInPlainText()
    {
        var lyrics = Parse("﻿Hello", "world");

        Assert.Equal(new[] { "Hello", "world" }, Texts(lyrics));
    }

    [Fact]
    public void Parse_SameTimeLines_BecomeExtraWithoutDuplicates()
    {
        var lyrics = Parse(
            "[00:01.00]Hola",
            "[00:01.00]Hello",
            "[00:01.00]Hello",
            "[00:01.00]Hola",
            "[00:01.00]",
            "[00:05.00]Adiós");

        Assert.Equal(new[] { "Hola", "Adiós" }, Texts(lyrics));
        Assert.Equal(new[] { "Hello" }, lyrics.Lines[0].Extra);
        Assert.Empty(lyrics.Lines[1].Extra);
        Assert.Null(lyrics.Lines[0].Reading);
        Assert.Null(lyrics.Lines[0].Translation);
    }

    [Fact]
    public void Parse_BlankFirstLineAtSameTime_TakesTheNextText()
    {
        var lyrics = Parse("[00:01.00]", "[00:01.00]a", "[00:01.00]b", "[00:02.00]c");

        Assert.Equal(new[] { "a", "c" }, Texts(lyrics));
        Assert.Equal(new[] { "b" }, lyrics.Lines[0].Extra);
    }

    [Fact]
    public void Parse_ThreeLayerLrc_FillsReadingAndTranslation()
    {
        var lyrics = Parse(
            "[00:02.35]もうちょっとだから",
            "[00:02.35]Mou chotto dakara",
            "[00:02.35]Karena tinggal sedikit lagi",
            "[00:05.00]Light, light",
            "[00:05.00]light, light",
            "[00:05.00]Cahaya, cahaya",
            "[00:08.00]光の中へ",
            "[00:08.00]Hikari no naka e",
            "[00:08.00]Ke dalam cahaya");

        Assert.Equal(new[] { "もうちょっとだから", "Light, light", "光の中へ" }, Texts(lyrics));
        // Lapisan yang sama dengan liriknya (beda huruf besar-kecil saja) tidak ditulis ulang
        Assert.Equal(new[] { "Mou chotto dakara", null, "Hikari no naka e" }, Readings(lyrics));
        Assert.Equal(new[] { "Karena tinggal sedikit lagi", "Cahaya, cahaya", "Ke dalam cahaya" }, Translations(lyrics));
        Assert.All(lyrics.Lines, l => Assert.Empty(l.Extra));
    }

    [Fact]
    public void Parse_ThreeLayerLrc_ToleratesSingleLinesAndInterludes()
    {
        var lyrics = Parse(
            "[00:01.00]一", "[00:01.00]ichi", "[00:01.00]satu",
            "[00:02.00]二", "[00:02.00]", "[00:02.00]dua",
            "[00:03.00]三", "[00:03.00]san", "[00:03.00]tiga",
            "[00:04.00]Yeah",
            "[00:10.00]");

        Assert.Equal(new[] { "一", "二", "三", "Yeah", "" }, Texts(lyrics));
        Assert.Equal(new[] { "ichi", null, "san", null, null }, Readings(lyrics));
        Assert.Equal(new[] { "satu", "dua", "tiga", null, null }, Translations(lyrics));
        Assert.All(lyrics.Lines, l => Assert.Empty(l.Extra));
    }

    [Fact]
    public void Parse_ThreeLayerLrc_WithRepeatedChorusTimes()
    {
        // Reff yang ditulis sekali untuk dua waktu tetap terbaca tiga lapis di kedua pengulangannya
        var lyrics = Parse(
            "[00:10.00][01:20.00]サビ", "[00:10.00][01:20.00]sabi", "[00:10.00][01:20.00]reff",
            "[00:20.00]歌", "[00:20.00]uta", "[00:20.00]lagu");

        Assert.Equal(new[] { "サビ", "歌", "サビ" }, Texts(lyrics));
        Assert.Equal(new[] { "sabi", "uta", "sabi" }, Readings(lyrics));
        Assert.Equal(new[] { "reff", "lagu", "reff" }, Translations(lyrics));
    }

    [Fact]
    public void Parse_TwoLayerLrc_StaysExtra()
    {
        // Baris kedua bisa cara baca atau terjemahan, jadi tidak ditebak
        var lyrics = Parse(
            "[00:01.00]一", "[00:01.00]satu",
            "[00:02.00]二", "[00:02.00]dua");

        Assert.Equal(new[] { "satu" }, lyrics.Lines[0].Extra);
        Assert.Equal(new[] { "dua" }, lyrics.Lines[1].Extra);
        Assert.All(lyrics.Lines, l => Assert.Null(l.Reading));
        Assert.All(lyrics.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void Parse_FileWithFourLineGroups_IsNotLayered()
    {
        var lyrics = Parse(
            "[00:01.00]一", "[00:01.00]ichi", "[00:01.00]satu",
            "[00:02.00]二", "[00:02.00]ni", "[00:02.00]dua",
            "[00:03.00]三", "[00:03.00]san", "[00:03.00]tiga",
            "[00:04.00]四", "[00:04.00]yon", "[00:04.00]empat", "[00:04.00]four");

        Assert.Equal(new[] { "ichi", "satu" }, lyrics.Lines[0].Extra);
        Assert.Equal(new[] { "yon", "empat", "four" }, lyrics.Lines[3].Extra);
        Assert.All(lyrics.Lines, l => Assert.Null(l.Reading));
        Assert.All(lyrics.Lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void Parse_FewThreeLineGroups_IsNotLayered()
    {
        // Hanya 2 dari 4 baris yang punya tiga lapis, kurang dari 3/4
        var lyrics = Parse(
            "[00:01.00]a", "[00:01.00]a1", "[00:01.00]a2",
            "[00:02.00]b", "[00:02.00]b1", "[00:02.00]b2",
            "[00:03.00]c",
            "[00:04.00]d");

        Assert.Equal(new[] { "a1", "a2" }, lyrics.Lines[0].Extra);
        Assert.All(lyrics.Lines, l => Assert.Null(l.Reading));
        Assert.All(lyrics.Lines, l => Assert.Null(l.Translation));
    }
}

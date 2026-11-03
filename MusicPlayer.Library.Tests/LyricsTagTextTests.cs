using static MusicPlayer.Library.Tests.TestLyrics;

namespace MusicPlayer.Library.Tests;

public class LyricsTagTextTests
{
    // LRC "enhanced" dengan baris info, seperti yang ditulis aplikasi karaoke lain ke tag LYRICS
    private const string Enhanced =
        "[ti:Song]\n[ar:Artist]\n[by:Someone]\n[length:03:30]\n"
        + "[00:12.00]<00:12.00>もう <00:12.40>ちょっと <00:12.90>だから<00:13.60>\n"
        + "[00:14.00]<00:14.00>Light, <00:14.50>light<00:15.10>";

    [Fact]
    public void ForSave_OnlyLayersEdited_DoesNotRewriteTag()
    {
        var edited = WithLayer(LrcParser.Parse(Enhanced)!, 0, reading: "Mou chotto dakara", translation: "Tinggal sedikit lagi");

        Assert.Null(LyricsTagText.ForSave(edited, Enhanced, originalInTag: true));
    }

    [Fact]
    public void ForSave_PlainTagUnchanged_DoesNotRewriteTag()
    {
        const string tag = "First verse\n\n\ngoes here\n\n";
        var edited = WithLayer(LrcParser.Parse(tag)!, 2, reading: null, translation: "di sini");

        Assert.Null(LyricsTagText.ForSave(edited, tag, originalInTag: true));
    }

    [Fact]
    public void ForSave_SourceNotInTag_WritesWithHeader()
    {
        // Lirik dari file .lrc atau teks yang ditempel: tag LYRICS belum berisi lirik ini, jadi selalu ditulis
        var edited = LrcParser.Parse(Enhanced)!;

        string? text = LyricsTagText.ForSave(edited, Enhanced, originalInTag: false);

        Assert.Equal(Enhanced, text);
    }

    [Fact]
    public void ForSave_LineEdited_KeepsHeaderAndWordTimesOfOtherLines()
    {
        var stored = LrcParser.Parse(Enhanced)!;
        var edited = new Lyrics([stored.Lines[0], stored.Lines[1] with { Text = "Light, light, light" }], isSynced: true);

        string? text = LyricsTagText.ForSave(edited, Enhanced, originalInTag: true);

        Assert.Equal(
            "[ti:Song]\n[ar:Artist]\n[by:Someone]\n[length:03:30]\n"
            + "[00:12.00]<00:12.00>もう <00:12.40>ちょっと <00:12.90>だから<00:13.60>\n"
            + "[00:14.00]Light, light, light",
            text);
        AssertSameLines(edited, LrcParser.Parse(text));
    }

    [Fact]
    public void ForSave_NoLines_RemovesLyrics() =>
        Assert.Equal("", LyricsTagText.ForSave(new Lyrics([], isSynced: false), Enhanced, originalInTag: true));

    [Fact]
    public void ForSave_NoLinesAndNoOriginal_WritesNothing() =>
        Assert.Null(LyricsTagText.ForSave(new Lyrics([], isSynced: false), null, originalInTag: false));

    [Fact]
    public void ForSave_ThreeLayerTag_LayerChanged_DoesNotRewriteTag()
    {
        // Isian di tag LYRICS_READING/LYRICS_TRANSLATION menang dari lapisan LRC-nya (LyricLayers.Attach)
        const string tag = "[00:02.35]もうちょっとだから\n[00:02.35]Mou chotto dakara\n[00:02.35]Karena tinggal sedikit lagi";
        var edited = WithLayer(LrcParser.Parse(tag)!, 0, reading: "Mō chotto dakara", translation: "Karena tinggal sedikit lagi");

        Assert.Null(LyricsTagText.ForSave(edited, tag, originalInTag: true));
    }

    [Fact]
    public void ForSave_ThreeLayerTag_LayerCleared_RewritesWithoutLayerLines()
    {
        // Cara baca yang dikosongkan akan muncul lagi dari LRC tiga lapisnya, jadi liriknya ditulis ulang tanpa baris
        // lapisan; terjemahannya tersimpan di tag LYRICS_TRANSLATION
        const string tag = "[00:02.35]もうちょっとだから\n[00:02.35]Mou chotto dakara\n[00:02.35]Karena tinggal sedikit lagi";
        var edited = WithLayer(LrcParser.Parse(tag)!, 0, reading: null, translation: "Karena tinggal sedikit lagi");

        Assert.Equal("[00:02.35]もうちょっとだから", LyricsTagText.ForSave(edited, tag, originalInTag: true));
    }

    [Fact]
    public void Format_Offset_IsDroppedAndWordTimesShifted()
    {
        // Offset +500: tanda waktu baris sudah digeser LrcParser, tanda waktu per kata digeser sama banyak
        const string original = "[ar:Artist]\n[offset:+500]\n[00:12.00]<00:12.00>もう <00:12.40>ちょっと\n[00:14.00]Light";
        var lyrics = LrcParser.Parse(original)!;

        string text = LyricsTagText.Format(lyrics, original);

        Assert.Equal("[ar:Artist]\n[00:11.50]<00:11.50>もう <00:11.90>ちょっと\n[00:13.50]Light", text);
        AssertSameLines(lyrics, LrcParser.Parse(text));
    }

    [Fact]
    public void Format_RepeatedLine_WordTimesNotKept()
    {
        // Tanda waktu per kata di baris yang dipakai untuk dua waktu hanya benar untuk salah satunya
        const string original = "[00:45.00][01:50.00]<00:45.00>la <00:45.50>la";
        var lyrics = LrcParser.Parse(original)!;

        Assert.Equal("[00:45.00]la la\n[01:50.00]la la", LyricsTagText.Format(lyrics, original));
    }

    [Fact]
    public void Format_ExtraLineWithWordTimes_IsKept()
    {
        const string original = "[00:01.00]<00:01.00>Hola <00:01.50>amigo\n[00:01.00]<00:01.00>Hello <00:01.50>friend";
        var lyrics = LrcParser.Parse(original)!;

        string text = LyricsTagText.Format(lyrics, original);

        Assert.Equal(original, text);
        AssertSameLines(lyrics, LrcParser.Parse(text));
    }

    [Fact]
    public void Format_PlainLyrics_KeepsHeader()
    {
        const string original = "[ti:Song]\n[au:Writer]\nFirst verse\n\ngoes here";
        var lyrics = LrcParser.Parse(original)!;

        Assert.Equal(original, LyricsTagText.Format(lyrics, original));
    }

    [Fact]
    public void Format_NoOriginal_SameAsLrcWriter()
    {
        var lyrics = Synced(new LyricLine(At(1.5), "Hola") { Extra = ["Hello"] }, Line(10, ""), Line(83.45, "Adiós"));

        Assert.Equal(LrcWriter.Format(lyrics), LyricsTagText.Format(lyrics, null));
        Assert.Equal(LrcWriter.Format(lyrics), LyricsTagText.Format(lyrics, "  "));
    }

    [Fact]
    public void Format_InfoLikeLyricLines_StayLyrics()
    {
        // "[Chorus]" dan "[Intro: Drake]" bukan tag info LRC: tetap baris lirik, tidak dipindah ke atas
        const string original = "[00:01.00][Intro: Drake]\n[00:02.00]<00:02.00>Yeah\n[00:03.00][Chorus]";
        var lyrics = LrcParser.Parse(original)!;

        string text = LyricsTagText.Format(lyrics, original);

        Assert.Equal(original, text);
        AssertSameLines(lyrics, LrcParser.Parse(text));
    }

    private static Lyrics WithLayer(Lyrics lyrics, int index, string? reading, string? translation) =>
        new(lyrics.Lines.Select((l, i) => i == index ? l with { Reading = reading, Translation = translation } : l).ToList(),
            lyrics.IsSynced);

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

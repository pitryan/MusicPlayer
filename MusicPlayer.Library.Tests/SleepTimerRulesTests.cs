using MusicPlayer.Library;

namespace MusicPlayer.Library.Tests;

public sealed class SleepTimerRulesTests
{
    private static Track Song(string name, string? album, string? albumArtist = "LiSA", string? artist = "LiSA", string folder = @"C:\Music\A") =>
        new() { FilePath = $@"{folder}\{name}.flac", FolderPath = folder, Title = name, Album = album, AlbumArtist = albumArtist, Artist = artist };

    [Fact]
    public void Off_And_Timed_NeverStopAtSongEnd()
    {
        var a = Song("1", "oath sign");
        var b = Song("2", "Other");
        Assert.False(SleepTimerRules.StopsAfter(SleepTimerMode.Off, a, b));
        Assert.False(SleepTimerRules.StopsAfter(SleepTimerMode.Timed, a, b));
    }

    [Fact]
    public void EndOfSong_AlwaysStops()
    {
        var a = Song("1", "oath sign");
        Assert.True(SleepTimerRules.StopsAfter(SleepTimerMode.EndOfSong, a, Song("2", "oath sign")));
        Assert.True(SleepTimerRules.StopsAfter(SleepTimerMode.EndOfSong, a, a)); // Repeat One
        Assert.True(SleepTimerRules.StopsAfter(SleepTimerMode.EndOfSong, a, null));
    }

    [Fact]
    public void EndOfAlbum_StopsWhenTheNextSongIsFromAnotherAlbum()
    {
        var a = Song("1", "oath sign");
        Assert.False(SleepTimerRules.StopsAfter(SleepTimerMode.EndOfAlbum, a, Song("2", "OATH SIGN ")));
        Assert.True(SleepTimerRules.StopsAfter(SleepTimerMode.EndOfAlbum, a, Song("2", "crossing field")));
        Assert.True(SleepTimerRules.StopsAfter(SleepTimerMode.EndOfAlbum, a, null));
    }

    [Fact]
    public void SameAlbum_NeedsTheSameAlbumArtist()
    {
        var a = Song("1", "Greatest Hits", albumArtist: "LiSA");
        Assert.False(SleepTimerRules.SameAlbum(a, Song("2", "Greatest Hits", albumArtist: "ReoNa")));
        // Tanpa artis album, artis lagunya yang dibandingkan
        Assert.True(SleepTimerRules.SameAlbum(Song("1", "Single", albumArtist: null, artist: "Ayase"),
            Song("2", "Single", albumArtist: null, artist: "Ayase")));
    }

    [Fact]
    public void SameAlbum_WithoutAlbumName_UsesTheFolder()
    {
        Assert.True(SleepTimerRules.SameAlbum(Song("1", null), Song("2", "")));
        Assert.False(SleepTimerRules.SameAlbum(Song("1", null), Song("2", null, folder: @"C:\Music\B")));
        Assert.False(SleepTimerRules.SameAlbum(Song("1", null), Song("2", "oath sign")));
    }
}

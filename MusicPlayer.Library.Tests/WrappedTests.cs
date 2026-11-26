using MusicPlayer.Library;

namespace MusicPlayer.Library.Tests;

public sealed class WrappedTests
{
    // UTC+7 (WIB), tanpa pergantian musim
    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");

    private static Track Song(string name, string album, string artist, string? genre = "J-Pop", int minutes = 4,
        string codec = "FLAC", int bits = 16, int rate = 44100) => new()
    {
        FilePath = $@"C:\Music\{artist}\{album}\{name}.flac",
        FolderPath = $@"C:\Music\{artist}\{album}",
        Title = name,
        Album = album,
        AlbumArtist = artist,
        Artist = artist,
        Genre = genre,
        Duration = TimeSpan.FromMinutes(minutes),
        Codec = codec,
        BitsPerSample = bits,
        SampleRate = rate,
    };

    /// <summary>Jam setempat (WIB) menjadi waktu UTC yang disimpan database.</summary>
    private static PlayRecord Play(Track track, int year, int month, int day, int hour = 20) =>
        new(track, new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-7));

    [Fact]
    public void Build_RanksSongsArtistsAlbumsAndGenres()
    {
        var catchMoment = Song("Catch the Moment", "Catch the Moment", "LiSA");
        var oathSign = Song("oath sign", "oath sign", "LiSA");
        var ignite = Song("IGNITE", "IGNITE", "藍井エイル", genre: "Anime");
        var plays = new List<PlayRecord>
        {
            Play(catchMoment, 2026, 10, 20), Play(catchMoment, 2026, 10, 21), Play(catchMoment, 2026, 11, 1),
            Play(oathSign, 2026, 10, 22),
            Play(ignite, 2026, 10, 23), Play(ignite, 2026, 10, 24),
            Play(ignite, 2025, 12, 31), // tahun lain
        };

        var report = Wrapped.Build(plays, 2026, Jakarta);

        Assert.Equal(6, report.Plays);
        Assert.Equal(3, report.Songs);
        Assert.Equal(2, report.Artists);
        Assert.Equal(TimeSpan.FromMinutes(24), report.ListeningTime);
        Assert.Equal(new[] { "Catch the Moment", "IGNITE", "oath sign" }, report.TopSongs.Select(s => s.Track.Title));
        Assert.Equal(3, report.TopSongs[0].Plays);
        Assert.Equal("LiSA", report.TopArtists[0].Name);
        Assert.Equal(4, report.TopArtists[0].Plays);
        // Cover artis: lagunya yang paling sering diputar
        Assert.Equal(catchMoment.FilePath, report.TopArtists[0].CoverTrackPath);
        Assert.Equal(("Catch the Moment", "LiSA", 3), (report.TopAlbums[0].Title, report.TopAlbums[0].Artist, report.TopAlbums[0].Plays));
        Assert.Equal("J-Pop", report.TopGenres[0].Name);
        Assert.Equal(4.0 / 6, report.TopGenres[0].Share, 3);
    }

    [Fact]
    public void Build_UsesLocalDatesAndHours()
    {
        var song = Song("A", "Album", "Artist");
        // 31 Desember 23.30 WIB = 31 Desember 16.30 UTC; 1 Januari 01.00 WIB = 31 Desember 18.00 UTC
        var lateNight = new PlayRecord(song, new DateTime(2025, 12, 31, 18, 0, 0, DateTimeKind.Utc));
        var newYearsEve = new PlayRecord(song, new DateTime(2025, 12, 31, 16, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { 2026, 2025 }, Wrapped.Years([lateNight, newYearsEve], Jakarta));
        var report = Wrapped.Build([lateNight, newYearsEve], 2026, Jakarta);
        Assert.Equal(1, report.Plays);
        Assert.Equal(1, report.PlaysByMonth[0]);
        Assert.Equal(DayPart.Night, report.FavouriteDayPart);
    }

    [Fact]
    public void Build_FindsHabits()
    {
        var a = Song("A", "Album", "Artist");
        var b = Song("B", "Album", "Artist");
        var plays = new List<PlayRecord>
        {
            Play(b, 2026, 3, 1, hour: 9),
            Play(a, 2026, 3, 2), Play(a, 2026, 3, 2), Play(a, 2026, 3, 2), Play(b, 2026, 3, 2),
            Play(a, 2026, 3, 3),
            Play(a, 2026, 3, 10),
        };

        var report = Wrapped.Build(plays, 2026, Jakarta);

        Assert.Equal(b, report.FirstPlay!.Track);
        Assert.Equal(new DateOnly(2026, 3, 2), report.BusiestDay);
        Assert.Equal(4, report.BusiestDayPlays);
        Assert.Equal(4, report.DaysListened);
        Assert.Equal(3, report.LongestStreak);
        Assert.Equal(new RepeatDay(a, new DateOnly(2026, 3, 2), 3), report.OnRepeat);
        Assert.Equal(DayPart.Evening, report.FavouriteDayPart);
        Assert.Equal(7, report.PlaysByMonth[2]);
    }

    [Fact]
    public void Build_CountsQuality()
    {
        var hiRes = Song("H", "Album", "Artist", bits: 24, rate: 96000);
        var cd = Song("C", "Album", "Artist");
        var mp3 = Song("M", "Album", "Artist", codec: "MP3");

        var report = Wrapped.Build([Play(hiRes, 2026, 1, 1), Play(hiRes, 2026, 1, 2), Play(cd, 2026, 1, 3), Play(mp3, 2026, 1, 4)], 2026, Jakarta);

        Assert.Equal(2, report.PlaysByQuality[QualityKind.HiRes]);
        Assert.Equal(1, report.PlaysByQuality[QualityKind.Lossless]);
        Assert.Equal(1, report.PlaysByQuality[QualityKind.Lossy]);
    }

    [Fact]
    public void Build_EmptyYear()
    {
        var report = Wrapped.Build([], 2026, Jakarta);
        Assert.Equal(0, report.Plays);
        Assert.Empty(report.TopSongs);
        Assert.Null(report.FirstPlay);
        Assert.Null(report.FavouriteDayPart);
        Assert.Null(report.OnRepeat);
        Assert.Equal(0, report.LongestStreak);
    }
}

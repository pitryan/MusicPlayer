namespace MusicPlayer.Library;

public sealed record RankedTrack(Track Track, int Plays);

/// <summary>Artis dengan nama yang sama seperti di halaman Artists (ArtistNames.Primary dari artis album).</summary>
public sealed record RankedArtist(string Name, int Plays, string CoverTrackPath)
{
    public string DisplayName => Name.Length > 0 ? Name : "Unknown Artist";
}

/// <summary>Album dengan kunci yang sama seperti di halaman Albums (judul dan artis album).</summary>
public sealed record RankedAlbum(string Title, string Artist, int Plays, string CoverTrackPath);

public sealed record RankedGenre(string Name, int Plays, double Share);

public enum DayPart
{
    /// <summary>05.00–11.59</summary>
    Morning,

    /// <summary>12.00–16.59</summary>
    Afternoon,

    /// <summary>17.00–21.59</summary>
    Evening,

    /// <summary>22.00–04.59</summary>
    Night,
}

/// <summary>Satu lagu yang diputar berkali-kali di hari yang sama.</summary>
public sealed record RepeatDay(Track Track, DateOnly Day, int Plays);

/// <summary>Ringkasan setahun mendengarkan (lihat <see cref="Wrapped.Build"/>).</summary>
public sealed record WrappedReport(
    int Year,
    int Plays,
    int Songs,
    int Artists,
    TimeSpan ListeningTime,
    IReadOnlyList<RankedTrack> TopSongs,
    IReadOnlyList<RankedArtist> TopArtists,
    IReadOnlyList<RankedAlbum> TopAlbums,
    IReadOnlyList<RankedGenre> TopGenres,
    IReadOnlyList<int> PlaysByMonth,
    IReadOnlyDictionary<DayPart, int> PlaysByDayPart,
    PlayRecord? FirstPlay,
    DateOnly? BusiestDay,
    int BusiestDayPlays,
    int DaysListened,
    int LongestStreak,
    RepeatDay? OnRepeat,
    IReadOnlyDictionary<QualityKind, int> PlaysByQuality)
{
    public DayPart? FavouriteDayPart => Plays == 0 ? null : PlaysByDayPart.MaxBy(p => p.Value).Key;
}

/// <summary>
/// Wrapped: lagu, artis, album, dan genre yang paling sering diputar dalam setahun, beserta kebiasaan mendengarkannya,
/// dari riwayat putar (satu putaran dicatat setelah separuh lagu didengarkan). Tanggal dan jam dihitung di zona waktu
/// <c>zone</c>, jadi lagu yang diputar jam 23.30 masuk hari dan bulan setempat.
/// </summary>
public static class Wrapped
{
    /// <summary>Lagu yang diulang sesering ini dalam sehari baru disebut "On Repeat".</summary>
    public const int RepeatThreshold = 3;

    /// <summary>Tahun-tahun yang punya riwayat putar, yang terbaru lebih dulu.</summary>
    public static IReadOnlyList<int> Years(IEnumerable<PlayRecord> plays, TimeZoneInfo zone) =>
        plays.Select(p => Local(p, zone).Year).Distinct().OrderDescending().ToList();

    public static WrappedReport Build(IEnumerable<PlayRecord> allPlays, int year, TimeZoneInfo zone, int topCount = 50)
    {
        var plays = allPlays
            .Select(p => (Play: p, Local: Local(p, zone)))
            .Where(p => p.Local.Year == year)
            .OrderBy(p => p.Play.PlayedUtc)
            .ToList();

        var topSongs = plays
            .GroupBy(p => p.Play.Track.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Track: g.Last().Play.Track, Plays: g.Count(), Last: g.Max(p => p.Play.PlayedUtc)))
            .OrderByDescending(s => s.Plays)
            .ThenByDescending(s => s.Last)
            .Take(topCount)
            .Select(s => new RankedTrack(s.Track, s.Plays))
            .ToList();

        var topArtists = Rank(plays.Select(p => p.Play), t => ArtistNames.Primary(t.AlbumArtist ?? t.Artist), topCount)
            .Select(g => new RankedArtist(g.Key, g.Plays, g.Cover))
            .ToList();

        var topAlbums = Rank(plays.Select(p => p.Play), t => $"{t.AlbumArtist ?? t.Artist}\u0001{t.Album}", topCount,
                t => !string.IsNullOrWhiteSpace(t.Album))
            .Select(g => new RankedAlbum(g.Track.Album!, g.Track.AlbumArtist ?? g.Track.Artist ?? "", g.Plays, g.Cover))
            .ToList();

        int withGenre = plays.Count(p => !string.IsNullOrWhiteSpace(p.Play.Track.Genre));
        var topGenres = Rank(plays.Select(p => p.Play), t => t.Genre!.Trim(), topCount, t => !string.IsNullOrWhiteSpace(t.Genre))
            .Select(g => new RankedGenre(g.Track.Genre!.Trim(), g.Plays, (double)g.Plays / withGenre))
            .ToList();

        var byMonth = new int[12];
        var byPart = Enum.GetValues<DayPart>().ToDictionary(p => p, _ => 0);
        var byQuality = Enum.GetValues<QualityKind>().ToDictionary(k => k, _ => 0);
        foreach (var (play, local) in plays)
        {
            byMonth[local.Month - 1]++;
            byPart[PartOf(local.Hour)]++;
            byQuality[AudioQuality.Describe(play.Track).Kind]++;
        }

        var days = plays.GroupBy(p => DateOnly.FromDateTime(p.Local)).ToList();
        var busiest = days.OrderByDescending(d => d.Count()).ThenBy(d => d.Key).FirstOrDefault();
        var onRepeat = days
            .SelectMany(d => d.GroupBy(p => p.Play.Track.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RepeatDay(g.First().Play.Track, d.Key, g.Count())))
            .Where(r => r.Plays >= RepeatThreshold)
            .OrderByDescending(r => r.Plays)
            .ThenBy(r => r.Day)
            .FirstOrDefault();

        return new WrappedReport(
            Year: year,
            Plays: plays.Count,
            Songs: plays.Select(p => p.Play.Track.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Artists: plays.Select(p => ArtistNames.Primary(p.Play.Track.AlbumArtist ?? p.Play.Track.Artist))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ListeningTime: TimeSpan.FromTicks(plays.Sum(p => p.Play.Track.Duration.Ticks)),
            TopSongs: topSongs,
            TopArtists: topArtists,
            TopAlbums: topAlbums,
            TopGenres: topGenres,
            PlaysByMonth: byMonth,
            PlaysByDayPart: byPart,
            FirstPlay: plays.Count > 0 ? plays[0].Play : null,
            BusiestDay: busiest?.Key,
            BusiestDayPlays: busiest?.Count() ?? 0,
            DaysListened: days.Count,
            LongestStreak: LongestStreak(days.Select(d => d.Key)),
            OnRepeat: onRepeat,
            PlaysByQuality: byQuality);
    }

    public static DayPart PartOf(int hour) => hour switch
    {
        >= 5 and < 12 => DayPart.Morning,
        >= 12 and < 17 => DayPart.Afternoon,
        >= 17 and < 22 => DayPart.Evening,
        _ => DayPart.Night,
    };

    /// <summary>
    /// Kelompok yang paling sering diputar. Cover kelompoknya diambil dari lagu yang paling sering diputar di
    /// dalamnya, jadi cover artis adalah album yang paling sering didengarkan.
    /// </summary>
    private static IEnumerable<(string Key, int Plays, Track Track, string Cover)> Rank(IEnumerable<PlayRecord> plays,
        Func<Track, string> keyOf, int topCount, Func<Track, bool>? include = null) =>
        plays
            .Where(p => include?.Invoke(p.Track) ?? true)
            .GroupBy(p => keyOf(p.Track), StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var favourite = g.GroupBy(p => p.Track.FilePath, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(s => s.Count())
                    .ThenByDescending(s => s.Max(p => p.PlayedUtc))
                    .First().First().Track;
                return (g.Key, Plays: g.Count(), Track: favourite, Cover: favourite.FilePath, Last: g.Max(p => p.PlayedUtc));
            })
            .OrderByDescending(g => g.Plays)
            .ThenByDescending(g => g.Last)
            .Take(topCount)
            .Select(g => (g.Key, g.Plays, g.Track, g.Cover));

    private static int LongestStreak(IEnumerable<DateOnly> days)
    {
        int longest = 0, current = 0;
        DateOnly? previous = null;
        foreach (var day in days.Order())
        {
            current = previous is { } p && p.AddDays(1) == day ? current + 1 : 1;
            longest = Math.Max(longest, current);
            previous = day;
        }
        return longest;
    }

    private static DateTime Local(PlayRecord play, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(play.PlayedUtc, DateTimeKind.Utc), zone);
}

namespace MusicPlayer.Library;

/// <summary>
/// Ringkasan satu album. Artist berisi album artist (atau artis lagu kalau album artist kosong),
/// dan dipakai bersama Title sebagai "kunci" album.
/// </summary>
public sealed record AlbumSummary(
    string Title,
    string Artist,
    int? Year,
    int TrackCount,
    TimeSpan Duration,
    string? Genre,
    string CoverTrackPath,
    DateTime AddedUtc)
{
    public string DisplayArtist => Artist.Length > 0 ? Artist : "Unknown Artist";

    /// <summary>Jenis rilis dari tag MusicBrainz, kalau ada (lihat Track.ReleaseType).</summary>
    public string? ReleaseTypeTag { get; init; }

    /// <summary>Jumlah lagu album menurut tag (TRACKTOTAL), yang bisa lebih banyak dari lagu yang dimiliki.</summary>
    public int? TrackTotal { get; init; }

    /// <summary>Durasi lagu terpanjang, untuk aturan Single Apple (setiap lagu di bawah 10 menit).</summary>
    public TimeSpan LongestTrack { get; init; }

    /// <summary>Tanggal rilis dari tag (teks apa adanya, misalnya "2025-02-07"), untuk mengurutkan rilis.</summary>
    public string? ReleaseDate { get; init; }

    public ReleaseKind Kind =>
        ReleaseKinds.Classify(Title, ReleaseTypeTag, TrackCount, TrackTotal, Duration, LongestTrack);

    /// <summary>Kunci urutan rilis, yang terbaru paling besar: tanggal rilis lengkap kalau ada, kalau tidak tahunnya.</summary>
    public string ReleaseSortKey =>
        ReleaseDate is { Length: >= 4 } date && char.IsDigit(date[0]) ? date : (Year?.ToString("D4") ?? "");
}

public enum ReleaseKind
{
    Album,
    Single,
    EP,
}

/// <summary>
/// Menentukan jenis rilis, berurutan: tag MusicBrainz (RELEASETYPE), akhiran judul " - Single" atau " - EP" (gaya
/// Apple Music dan banyak toko musik), lalu aturan Apple: Single 1–3 lagu yang masing-masing di bawah 10 menit;
/// EP 1–3 lagu dengan lagu 10 menit atau lebih tapi total di bawah 30 menit, atau 4–6 lagu dengan total di bawah
/// 30 menit; selain itu Album.
/// </summary>
public static class ReleaseKinds
{
    private static readonly System.Text.RegularExpressions.Regex TitleSuffix = new(
        @"\s-\s(Single|EP)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly TimeSpan SingleTrackLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShortReleaseLimit = TimeSpan.FromMinutes(30);

    /// <param name="ownedTracks">Jumlah lagu album ini yang ada di library.</param>
    /// <param name="trackTotal">Jumlah lagu menurut tag; dipakai kalau lebih besar, supaya album yang baru dimiliki
    /// sebagian lagunya tidak dianggap Single.</param>
    public static ReleaseKind Classify(string title, string? typeTag, int ownedTracks, int? trackTotal,
        TimeSpan totalDuration, TimeSpan longestTrack)
    {
        // RELEASETYPE bisa berisi beberapa jenis ("album; compilation", "album/soundtrack"); jenis utamanya yang pertama
        string primary = (typeTag ?? "").Split([';', '/', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant() ?? "";
        switch (primary)
        {
            case "single": return ReleaseKind.Single;
            case "ep": return ReleaseKind.EP;
            case "album": return ReleaseKind.Album;
        }

        var suffix = TitleSuffix.Match(title);
        if (suffix.Success)
            return suffix.Groups[1].Value.Equals("EP", StringComparison.OrdinalIgnoreCase) ? ReleaseKind.EP : ReleaseKind.Single;

        int count = Math.Max(ownedTracks, trackTotal ?? 0);
        if (count >= 7 || totalDuration >= ShortReleaseLimit)
            return ReleaseKind.Album;
        if (count <= 3 && longestTrack < SingleTrackLimit)
            return ReleaseKind.Single;
        return ReleaseKind.EP;
    }
}

/// <summary>Pengelompokan untuk halaman Recently Added, seperti di Apple Music.</summary>
public static class RecentlyAdded
{
    public static string GroupTitle(DateTime addedUtc, DateTime nowUtc)
    {
        var age = nowUtc - addedUtc;
        if (age <= TimeSpan.FromDays(7)) return "This Week";
        if (age <= TimeSpan.FromDays(31)) return "This Month";
        if (age <= TimeSpan.FromDays(92)) return "Last 3 Months";
        if (age <= TimeSpan.FromDays(183)) return "Last 6 Months";
        return addedUtc.ToLocalTime().Year.ToString();
    }
}

/// <summary>Pengelompokan untuk halaman Recently Played: menurut tanggal lokal, bukan selisih jam.</summary>
public static class RecentlyPlayed
{
    /// <summary>"Today", "Yesterday", "This Week", "This Month", atau "Earlier".</summary>
    public static string GroupTitle(DateTime playedUtc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var played = TimeZoneInfo.ConvertTimeFromUtc(playedUtc, zone).Date;
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        return (today - played).Days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => "This Week",
            < 31 => "This Month",
            _ => "Earlier",
        };
    }
}

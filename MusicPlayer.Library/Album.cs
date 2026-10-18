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

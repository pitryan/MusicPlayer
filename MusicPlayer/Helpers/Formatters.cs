using System;

namespace MusicPlayer.Helpers;

/// <summary>Fungsi format yang bisa dipanggil langsung dari x:Bind di XAML.</summary>
public static class Formatters
{
    /// <summary>Contoh: 3:07, atau 1:02:45 untuk durasi lebih dari satu jam.</summary>
    public static string Time(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    /// <summary>
    /// Kapan lagu diputar, dalam waktu lokal: jam saja kalau hari ini ("14:05"), "Yesterday 14:05",
    /// atau tanggalnya kalau lebih lama. Format jam dan tanggal mengikuti pengaturan Windows.
    /// </summary>
    public static string PlayedAt(DateTime playedUtc)
    {
        DateTime local = playedUtc.ToLocalTime();
        DateTime today = DateTime.Today;
        if (local.Date == today)
            return local.ToString("t");
        if (local.Date == today.AddDays(-1))
            return $"Yesterday {local:t}";
        return local.Year == today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
    }

    /// <summary>Contoh: "1 album", "3 albums", "12 songs".</summary>
    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Contoh: "YOASOBI — THE BOOK 3".</summary>
    public static string ArtistAndAlbum(string artist, string album) => $"{artist} — {album}";

    /// <summary>Nomor track, atau kosong kalau tag-nya tidak ada.</summary>
    public static string TrackNumber(int? number) => number?.ToString() ?? "";
}

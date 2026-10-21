using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;

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

    /// <summary>Angka volume untuk ditampilkan di samping slider, misalnya "65".</summary>
    public static string Percent(double value) => Math.Round(value).ToString();

    /// <summary>
    /// Tanggal rilis untuk kaki halaman album: "30 September 2026", "September 2026", atau "2026". Tag bisa berisi
    /// "2026-09-30", "2026-09", "2026", atau waktu lengkap seperti "2025-02-06T15:00:00Z" (bagian tanggalnya saja
    /// yang dipakai). Kalau tag kosong atau tidak dikenali, dipakai tahun albumnya.
    /// </summary>
    public static string ReleaseDate(string? tagValue, int? year)
    {
        var match = Regex.Match(tagValue ?? "", @"^\s*(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?");
        if (match.Success && int.Parse(match.Groups[1].Value) is int y && y >= 1)
        {
            if (!match.Groups[2].Success || !int.TryParse(match.Groups[2].Value, out int month) || month is < 1 or > 12)
                return y.ToString();

            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out int day)
                && day >= 1 && day <= DateTime.DaysInMonth(y, month))
                return new DateTime(y, month, day).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

            return new DateTime(y, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        }

        return year?.ToString() ?? "";
    }

    /// <summary>Ukuran file: "850 KB", "23.4 MB", "1.2 GB" (pemisah desimal mengikuti pengaturan Windows).</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    /// <summary>Tampil kalau true. Dipakai x:Bind untuk menukar dua ikon, misalnya Play dan Pause.</summary>
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Kebalikan <see cref="VisibleIf"/>.</summary>
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Contoh: "1 album", "3 albums", "12 songs".</summary>
    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Contoh: "YOASOBI — THE BOOK 3".</summary>
    public static string ArtistAndAlbum(string artist, string album) => $"{artist} — {album}";

    /// <summary>Nomor track, atau kosong kalau tag-nya tidak ada.</summary>
    public static string TrackNumber(int? number) => number?.ToString() ?? "";
}

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using MusicPlayer.Library;

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

    /// <summary>Copyright dari tag: "(P)" dan "(C)" ditampilkan sebagai ℗ dan ©, seperti di Apple Music.</summary>
    public static string Copyright(string text) =>
        Regex.Replace(Regex.Replace(text.Trim(), @"\(\s*[Pp]\s*\)", "℗"), @"\(\s*[Cc]\s*\)", "©");

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

    /// <summary>Golongan kualitas yang ringkas untuk badge di bar atas dan MiniPlayer.</summary>
    public static string QualityCompactText(bool hiRes, bool lossless, bool lossy) =>
        hiRes ? "Hi-Res" : lossless ? "LL" : lossy ? "Lossy" : "";

    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIfNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Frekuensi seperti "22 kHz", "16,8 kHz", atau "22,05 kHz" (batas CD ditulis tepat).</summary>
    public static string Kilohertz(double hz) => $"{hz / 1000.0:0.##} kHz";

    /// <summary>
    /// Lagu lossless yang patut dicurigai: hasil upsampling, dari sumber lossy, bandwidth terbatas, atau wadah 24-bit
    /// yang hanya berisi 16 bit.
    /// </summary>
    public static bool IsSuspicious(QualityReport report, Track track) =>
        AudioQuality.Describe(track).IsLossless
        && (report.Verdict is SourceVerdict.Upsampled or SourceVerdict.LikelyLossy or SourceVerdict.LimitedBandwidth
            || (track.BitsPerSample >= 24 && !report.IsFloat && report.EffectiveBits is <= 16));

    /// <summary>Kesimpulan pemeriksaan kualitas sumber, misalnya "Upsampled from 44.1 kHz" atau "True Hi-Res".</summary>
    public static string SourceVerdictText(QualityReport report, Track track)
    {
        // File lossy memang terpotong; spektrumnya tetap ditampilkan, tanpa "tuduhan"
        if (!AudioQuality.Describe(track).IsLossless)
            return "Lossy File";

        return report.Verdict switch
        {
            SourceVerdict.Genuine => report.SampleRate > 48000 ? "True Hi-Res" : "Full-Range Lossless",
            SourceVerdict.Upsampled => $"Upsampled from {AudioQuality.FormatSampleRate(report.LikelySourceRate ?? 0)}",
            SourceVerdict.LikelyLossy => "Likely from a Lossy Source",
            SourceVerdict.LimitedBandwidth => "Limited Bandwidth",
            _ => "Couldn't Judge",
        };
    }

    /// <summary>Penjelasan dan buktinya: di mana suara berhenti, dan berapa bit yang benar-benar terpakai.</summary>
    public static string SourceDetailText(QualityReport report, Track track)
    {
        bool lossless = AudioQuality.Describe(track).IsLossless;
        string cutoff = report.CutoffHz is { } hz ? Kilohertz(hz) : "";
        string sound = !lossless
            ? (cutoff.Length > 0 ? $"The encoded sound stops at {cutoff}." : "The encoded sound reaches the full range.")
            : report.Verdict switch
            {
                SourceVerdict.Genuine when report.SampleRate > 48000 => "Sound continues above 24 kHz.",
                SourceVerdict.Genuine => $"Sound reaches the full {Kilohertz(report.SampleRate / 2.0)} range.",
                SourceVerdict.Upsampled => $"Nothing above {cutoff}: the extra sample rate adds file size, not sound.",
                SourceVerdict.LikelyLossy => $"Sharp cutoff at {cutoff}, typical of MP3 or AAC encoding.",
                SourceVerdict.LimitedBandwidth => $"Sound stops at {cutoff}: an old recording, or a low-bitrate source.",
                _ => "The song is too quiet or too short to analyze.",
            };

        // Bit depth dibandingkan dengan yang tertulis di file (decoder Windows bisa memberi wadah 32-bit untuk 24-bit)
        string bits = "";
        if (lossless && !report.IsFloat && report.EffectiveBits is { } used)
        {
            int declared = track.BitsPerSample > 0 ? track.BitsPerSample : report.ContainerBits;
            bits = declared >= 24 && used <= 16
                ? $"Only 16 of the {declared} bits carry sound."
                : $"{Math.Min(used, declared)} bits used.";
        }
        return bits.Length > 0 ? $"{sound} {bits}" : sound;
    }

    public static string ReleaseKindLabel(ReleaseKind kind) => kind switch
    {
        ReleaseKind.Single => "Single",
        ReleaseKind.EP => "EP",
        _ => "Album",
    };

    /// <summary>Durasi total playlist seperti Apple Music: "47 minutes", "1 hour", "3 hours, 46 minutes".</summary>
    public static string LongDuration(TimeSpan total)
    {
        int minutes = (int)Math.Round(total.TotalMinutes);
        int hours = minutes / 60;
        minutes %= 60;
        if (hours == 0)
            return Count(minutes, "minute");
        return minutes == 0 ? Count(hours, "hour") : $"{Count(hours, "hour")}, {Count(minutes, "minute")}";
    }

    /// <summary>Contoh: "YOASOBI — THE BOOK 3".</summary>
    public static string ArtistAndAlbum(string artist, string album) => $"{artist} — {album}";

    /// <summary>Nomor track, atau kosong kalau tag-nya tidak ada.</summary>
    public static string TrackNumber(int? number) => number?.ToString() ?? "";
}

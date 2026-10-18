namespace MusicPlayer.Library;

/// <summary>Teks badge (misalnya "Hi-Res Lossless") dan detail kualitasnya (misalnya "24-bit 96 kHz FLAC").</summary>
public sealed record QualityInfo(string Badge, string Detail, bool IsLossless, bool IsHiRes)
{
    /// <summary>Versi pendek untuk tempat sempit seperti LCD: "Hi-Res", "Lossless", "MP3", "AAC".</summary>
    public string ShortBadge => IsHiRes ? "Hi-Res" : IsLossless ? "Lossless" : Badge.Split(' ')[0];
}

public static class AudioQuality
{
    private static readonly HashSet<string> LosslessCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "FLAC", "WAV", "AIFF", "ALAC", "APE", "WV" };

    /// <summary>
    /// Mengikuti istilah Apple Music: Lossless sampai 48 kHz, Hi-Res Lossless di atas 48 kHz.
    /// Format lossy (MP3, AAC) cukup ditampilkan jenis dan bitrate-nya.
    /// </summary>
    public static QualityInfo Describe(Track track)
    {
        // File .m4a bisa berisi AAC (lossy) atau ALAC (lossless). ALAC hampir selalu di atas 700 kbps.
        bool isM4a = string.Equals(track.Codec, "M4A", StringComparison.OrdinalIgnoreCase);
        bool lossless = LosslessCodecs.Contains(track.Codec) || (isM4a && track.BitrateKbps >= 700);
        string codec = isM4a ? (lossless ? "ALAC" : "AAC") : track.Codec;
        string rate = FormatSampleRate(track.SampleRate);

        if (lossless)
        {
            bool hiRes = track.SampleRate > 48000;
            string bits = track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "";
            return new QualityInfo(hiRes ? "Hi-Res Lossless" : "Lossless", Join(bits, rate, codec), true, hiRes);
        }

        string bitrate = track.BitrateKbps > 0 ? $"{track.BitrateKbps} kbps" : "";
        return new QualityInfo(Join(codec, bitrate), Join(codec, bitrate, rate), false, false);
    }

    /// <summary>Contoh: 44.1 kHz, 96 kHz, 176.4 kHz (pemisah desimal mengikuti pengaturan Windows).</summary>
    public static string FormatSampleRate(int sampleRate) =>
        sampleRate > 0 ? $"{sampleRate / 1000.0:0.#} kHz" : "";

    private static string Join(params string[] parts) =>
        string.Join(" ", parts.Where(p => p.Length > 0));
}

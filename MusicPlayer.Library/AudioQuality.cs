namespace MusicPlayer.Library;

/// <summary>Golongan kualitas file audio. Urutannya dari rendah ke tinggi.</summary>
public enum QualityKind
{
    /// <summary>Kompresi yang membuang sebagian data, misalnya MP3 dan AAC.</summary>
    Lossy,

    /// <summary>Tanpa data yang hilang, setara CD/DAT (16-bit, 44,1 atau 48 kHz) atau di bawahnya.</summary>
    Lossless,

    /// <summary>Lossless dengan bit depth atau sample rate di atas CD/DAT, menurut definisi JEITA.</summary>
    HiRes,
}

/// <summary>
/// Kualitas sebuah lagu untuk badge dan popup kualitas.
/// Label: "Hi-Res", "Lossless", atau "Lossy". Detail: "24-bit 96 kHz FLAC" atau "320 kbps MP3".
/// ShortBadge: teks badge kecil, yaitu "Hi-Res", "Lossless", atau nama codec lossy seperti "MP3".
/// </summary>
public sealed record QualityInfo(QualityKind Kind, string Label, string Detail, string ShortBadge)
{
    public bool IsLossless => Kind != QualityKind.Lossy;
    public bool IsHiRes => Kind == QualityKind.HiRes;
}

public static class AudioQuality
{
    private static readonly HashSet<string> LosslessCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "FLAC", "WAV", "AIFF", "ALAC", "APE", "WV" };

    /// <summary>
    /// Golongan Hi-Res mengikuti definisi JEITA yang juga dipakai Sony: file lossless yang bit depth atau sample
    /// rate-nya melebihi CD/DAT (16-bit, 44,1/48 kHz), tanpa ada yang di bawah CD. Jadi 24-bit 44,1 kHz dan
    /// 24-bit 48 kHz termasuk Hi-Res (berbeda dari Apple Music, yang baru menyebut Hi-Res di atas 48 kHz).
    /// Format lossy tidak pernah Hi-Res, dan ditampilkan bitrate serta codec-nya.
    /// </summary>
    public static QualityInfo Describe(Track track)
    {
        // File .m4a bisa berisi AAC (lossy) atau ALAC (lossless). ALAC hampir selalu di atas 700 kbps.
        bool isM4a = string.Equals(track.Codec, "M4A", StringComparison.OrdinalIgnoreCase);
        bool lossless = LosslessCodecs.Contains(track.Codec) || (isM4a && track.BitrateKbps >= 700);
        string codec = isM4a ? (lossless ? "ALAC" : "AAC") : track.Codec.ToUpperInvariant();

        if (!lossless)
        {
            string bitrate = track.BitrateKbps > 0 ? $"{track.BitrateKbps} kbps" : "";
            return new QualityInfo(QualityKind.Lossy, "Lossy", Join(bitrate, codec), codec);
        }

        string bits = track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "";
        string detail = Join(bits, FormatSampleRate(track.SampleRate), codec);
        return IsHiRes(track.BitsPerSample, track.SampleRate)
            ? new QualityInfo(QualityKind.HiRes, "Hi-Res", detail, "Hi-Res")
            : new QualityInfo(QualityKind.Lossless, "Lossless", detail, "Lossless");
    }

    /// <summary>
    /// Definisi JEITA: bit depth di atas 16 atau sample rate di atas 48 kHz, dan tidak ada yang di bawah CD
    /// (16-bit, 44,1 kHz). Bit depth yang tidak diketahui (0, misalnya pada sebagian file ALAC) dinilai dari
    /// sample rate saja.
    /// </summary>
    private static bool IsHiRes(int bits, int sampleRate) =>
        (bits > 16 || sampleRate > 48000) && (bits == 0 || bits >= 16) && sampleRate >= 44100;

    /// <summary>Contoh: 44.1 kHz, 96 kHz, 176.4 kHz (pemisah desimal mengikuti pengaturan Windows).</summary>
    public static string FormatSampleRate(int sampleRate) =>
        sampleRate > 0 ? $"{sampleRate / 1000.0:0.#} kHz" : "";

    private static string Join(params string[] parts) =>
        string.Join(" ", parts.Where(p => p.Length > 0));
}

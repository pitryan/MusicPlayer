namespace MusicPlayer.Library;

/// <summary>
/// Satu lagu di library. Nilai tag bisa null kalau file tidak menyimpannya;
/// pakai properti Display* untuk teks yang siap ditampilkan.
/// </summary>
public sealed record Track
{
    public long Id { get; init; }
    public required string FilePath { get; init; }
    public required string FolderPath { get; init; }
    public long FileSize { get; init; }
    public long ModifiedUtcTicks { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }

    public TimeSpan Duration { get; init; }
    public string Codec { get; init; } = "";
    public int SampleRate { get; init; }
    public int BitsPerSample { get; init; }
    public int Channels { get; init; }
    public int BitrateKbps { get; init; }
    public bool HasCover { get; init; }
    public bool HasLyrics { get; init; }

    /// <summary>Tanggal rilis apa adanya dari tag, misalnya "2026-09-30", "2026-09", atau "2026".</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Teks copyright dari tag, misalnya "℗ 2026 Sony Music Labels Inc.".</summary>
    public string? Copyright { get; init; }

    public string DisplayTitle => Title ?? Path.GetFileNameWithoutExtension(FilePath);
    public string DisplayArtist => Artist ?? AlbumArtist ?? "Unknown Artist";
    public string DisplayAlbum => Album ?? "Unknown Album";

    /// <summary>Di atas kualitas CD: lebih dari 16-bit atau lebih dari 48 kHz.</summary>
    public bool IsHiRes => BitsPerSample > 16 || SampleRate > 48000;

    /// <summary>Contoh: "FLAC 24/96" atau "MP3 320k".</summary>
    public string QualityLabel => BitsPerSample > 0
        ? $"{Codec} {BitsPerSample}/{SampleRate / 1000.0:0.#}"
        : $"{Codec} {BitrateKbps}k";
}

/// <summary>Ukuran dan waktu ubah terakhir sebuah file, untuk mendeteksi file yang berubah.</summary>
public readonly record struct FileStamp(long Size, long ModifiedUtcTicks)
{
    public static FileStamp From(FileInfo info) => new(info.Length, info.LastWriteTimeUtc.Ticks);
}

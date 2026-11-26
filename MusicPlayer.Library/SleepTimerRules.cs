namespace MusicPlayer.Library;

public enum SleepTimerMode
{
    Off,

    /// <summary>Pause setelah sekian menit, di tengah lagu sekalipun.</summary>
    Timed,

    /// <summary>Berhenti begitu lagu yang sedang diputar selesai.</summary>
    EndOfSong,

    /// <summary>Berhenti begitu lagu berikutnya berasal dari album lain (atau antrean habis).</summary>
    EndOfAlbum,
}

/// <summary>Kapan sleep timer menghentikan pemutaran di batas antar lagu.</summary>
public static class SleepTimerRules
{
    /// <summary>
    /// Pemutaran berhenti setelah <paramref name="current"/> selesai, bukan lanjut ke <paramref name="next"/> (lagu
    /// berikutnya di antrean, atau lagu ini lagi untuk Repeat One; null kalau antrean habis).
    /// </summary>
    public static bool StopsAfter(SleepTimerMode mode, Track current, Track? next) => mode switch
    {
        SleepTimerMode.EndOfSong => true,
        SleepTimerMode.EndOfAlbum => next is null || !SameAlbum(current, next),
        _ => false,
    };

    /// <summary>
    /// Dua lagu dari album yang sama: nama album dan artis albumnya sama. Lagu tanpa nama album dianggap satu album
    /// kalau ada di folder yang sama.
    /// </summary>
    public static bool SameAlbum(Track a, Track b)
    {
        if (string.IsNullOrWhiteSpace(a.Album) || string.IsNullOrWhiteSpace(b.Album))
            return string.IsNullOrWhiteSpace(a.Album) && string.IsNullOrWhiteSpace(b.Album)
                   && string.Equals(Path.GetDirectoryName(a.FilePath), Path.GetDirectoryName(b.FilePath), StringComparison.OrdinalIgnoreCase);

        return string.Equals(a.Album.Trim(), b.Album.Trim(), StringComparison.CurrentCultureIgnoreCase)
               && string.Equals((a.AlbumArtist ?? a.Artist)?.Trim(), (b.AlbumArtist ?? b.Artist)?.Trim(),
                   StringComparison.CurrentCultureIgnoreCase);
    }
}

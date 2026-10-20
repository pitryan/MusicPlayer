namespace MusicPlayer.Services;

/// <summary>Mode repeat, berganti setiap kali tombol repeat diklik: Off → All → One → Off.</summary>
public enum RepeatMode
{
    /// <summary>Berhenti setelah lagu terakhir di antrean.</summary>
    Off,

    /// <summary>Setelah lagu terakhir, seluruh antrean diputar lagi dari awal.</summary>
    All,

    /// <summary>Lagu yang sedang diputar diulang terus. Tombol Next tetap pindah ke lagu berikutnya.</summary>
    One,
}

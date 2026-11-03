namespace MusicPlayer.Helpers;

/// <summary>
/// Halaman editor yang bisa punya perubahan belum disimpan. Jendela utama menanyakan "Discard changes?" sebelum
/// meninggalkannya lewat back/forward, sidebar, pencarian, Go to Album/Artist, Settings, atau saat jendela ditutup.
/// Tombol Cancel milik halaman itu sendiri tidak ditanya: Cancel memang berarti membuang perubahan.
/// </summary>
public interface IUnsavedChanges
{
    /// <summary>
    /// True hanya kalau ada perubahan yang akan ditulis Save dan belum disimpan. False selama memuat, selama dan
    /// setelah menyimpan, dan kalau isinya sudah dikembalikan seperti semula.
    /// </summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// True selama Save masih menulis ke file. Halaman tidak boleh ditinggalkan dan jendela tidak boleh ditutup sampai
    /// selesai: pesan "Couldn't save" hanya tampil di halaman ini, dan proses yang berhenti memutus penulisan file.
    /// </summary>
    bool IsSaving { get; }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>Hasil menyimpan tag: lagu yang berhasil (data barunya) dan yang gagal beserta alasannya.</summary>
public sealed record TagSaveResult(IReadOnlyList<Track> Saved, IReadOnlyList<(string Path, string Error)> Failed);

/// <summary>
/// Menyimpan hasil editor tag: menulis ke file (lagu yang sedang diputar dilepas sebentar), membaca ulang lagunya ke
/// database (tanggal ditambahkan tetap), lalu memberi tahu tampilan lain lewat <see cref="LibraryChanged"/>.
/// Dipakai bersama lewat <see cref="App.TagEdits"/>.
/// </summary>
public sealed class TagEditService
{
    private readonly LibraryDatabase _database;
    private readonly PlaybackService _playback;
    private readonly Action<IEnumerable<string>> _invalidateCovers;
    private readonly Action _reloadLyrics;

    /// <param name="invalidateCovers">Melupakan cover lagu yang artwork-nya diganti (CoverCache.Invalidate).</param>
    /// <param name="reloadLyrics">Memuat ulang lirik lagu yang sedang diputar (App.Lyrics.Retry). Keduanya berupa
    /// parameter supaya layanan ini bisa diuji tanpa tampilan WinUI.</param>
    public TagEditService(LibraryDatabase database, PlaybackService playback,
        Action<IEnumerable<string>> invalidateCovers, Action reloadLyrics)
    {
        _database = database;
        _playback = playback;
        _invalidateCovers = invalidateCovers;
        _reloadLyrics = reloadLyrics;
    }

    /// <summary>Naik setiap kali isi library diubah editor; halaman yang di-cache memakainya untuk tahu kapan memuat ulang.</summary>
    public int Version { get; private set; }

    public event EventHandler? LibraryChanged;

    /// <summary>
    /// Menulis <paramref name="changes"/> (dan gambar, kalau tidak null) ke semua lagu. Satu lagu yang gagal (misalnya
    /// file read-only) tidak menghentikan lagu lainnya.
    /// </summary>
    public Task<TagSaveResult> SaveAsync(IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> changes,
        IReadOnlyList<TagPicture>? pictures, bool frontCoverOnly = false) =>
        SaveEachAsync(paths.Select(p => (p, changes)).ToList(), pictures, frontCoverOnly);

    /// <summary>
    /// Seperti SaveAsync, tapi setiap lagu dengan perubahannya sendiri, misalnya judul dan nomor track dari MusicBrainz.
    /// </summary>
    /// <remarks>
    /// <paramref name="frontCoverOnly"/>: gambar yang diberikan hanya menggantikan cover depan; cover belakang, booklet,
    /// dan gambar lain di file tetap (TagEditor.Write).
    /// </remarks>
    public async Task<TagSaveResult> SaveEachAsync(IReadOnlyList<(string Path, IReadOnlyDictionary<string, string> Changes)> edits,
        IReadOnlyList<TagPicture>? pictures, bool frontCoverOnly = false)
    {
        var paths = edits.Select(e => e.Path).ToList();
        var failed = new List<(string Path, string Error)>();
        await _playback.WithFilesReleasedAsync(paths, () => Task.Run(() =>
        {
            foreach (var (path, changes) in edits)
            {
                try
                {
                    TagEditor.Write(path, changes, pictures, frontCoverOnly);
                }
                catch (Exception ex)
                {
                    lock (failed)
                        failed.Add((path, Describe(ex)));
                }
            }
        }));

        // Data baru dibaca dari file yang sudah ditulis, jadi database sama persis dengan isi file
        var failedPaths = new HashSet<string>(failed.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var saved = await Task.Run(() =>
        {
            // Tag yang gagal dibaca ulang (misalnya file langsung dibuka aplikasi lain) tidak disimpan, supaya data lama
            // tidak tertimpa data kosong. Stempel data lama sudah beda dengan file, jadi scan berikutnya membaca tag
            // barunya; lagu yang belum ada di database juga akan ditemukan scan berikutnya.
            var tracks = paths.Where(p => !failedPaths.Contains(p)).Select(path =>
            {
                var info = new FileInfo(path);
                var track = TagReader.Read(path, new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks), out string? error);
                return error is null ? track : null;
            }).OfType<Track>().ToList();
            _database.UpsertTracks(tracks);
            return tracks;
        });

        if (pictures is not null)
            _invalidateCovers(saved.Select(t => t.FilePath));
        // Data lama lagu yang sedang diputar diambil sebelum diganti, untuk dibandingkan dengan data barunya
        var playing = _playback.CurrentTrack;
        _playback.UpdateTrackInfo(saved);
        if (playing is not null && LyricsNeedReload(playing, edits, saved))
            _reloadLyrics();

        if (saved.Count > 0)
        {
            Version++;
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        return new TagSaveResult(saved, failed);
    }

    /// <summary>
    /// Lirik lagu yang sedang diputar perlu dimuat ulang: liriknya, cara baca, atau terjemahannya diedit, atau judul,
    /// artis, artis album, atau albumnya berubah. Keempatnya bahan pencarian LRCLIB dan kunci cache-nya, jadi lirik yang
    /// tidak ditemukan atau salah lagu karena tag yang salah dicari ulang dengan tag yang sudah dibetulkan. Tanpa ini
    /// lirik lama tetap tampil: LyricsController tidak memuat ulang saat data lagu yang sama diganti (lihat Refresh).
    /// </summary>
    private static bool LyricsNeedReload(Track playing,
        IReadOnlyList<(string Path, IReadOnlyDictionary<string, string> Changes)> edits, IReadOnlyList<Track> saved)
    {
        bool Same(string path) => string.Equals(path, playing.FilePath, StringComparison.OrdinalIgnoreCase);

        // Lagu yang gagal disimpan atau dibaca ulang tidak berubah di aplikasi
        if (saved.FirstOrDefault(t => Same(t.FilePath)) is not { } updated)
            return false;

        // Tag yang dibaca bersama lirik dari file (LyricsFinder.FindLocal)
        return edits.Any(e => Same(e.Path) && e.Changes.Keys.Any(k => k is "lyrics" or "lyrics_reading" or "lyrics_translation"))
            || updated.Title != playing.Title || updated.Artist != playing.Artist
            || updated.AlbumArtist != playing.AlbumArtist || updated.Album != playing.Album;
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "The file is read-only, or this app isn't allowed to change it.",
        IOException io when io.HResult == unchecked((int)0x80070020) => "The file is in use by another app.",
        FileNotFoundException => "The file no longer exists.",
        _ => ex.Message,
    };
}

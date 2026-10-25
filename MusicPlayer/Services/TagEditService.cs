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
            var tracks = paths.Where(p => !failedPaths.Contains(p)).Select(path =>
            {
                var info = new FileInfo(path);
                return TagReader.Read(path, new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks), out _);
            }).ToList();
            _database.UpsertTracks(tracks);
            return tracks;
        });

        if (pictures is not null)
            _invalidateCovers(saved.Select(t => t.FilePath));
        _playback.UpdateTrackInfo(saved);
        bool lyricsChanged = edits.Any(e => e.Changes.ContainsKey("lyrics"));
        if (lyricsChanged && _playback.CurrentTrack is { } current
            && saved.Any(t => string.Equals(t.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase)))
            _reloadLyrics(); // lirik lagu yang sedang diputar baru saja diubah

        if (saved.Count > 0)
        {
            Version++;
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        return new TagSaveResult(saved, failed);
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "The file is read-only, or this app isn't allowed to change it.",
        IOException io when io.HResult == unchecked((int)0x80070020) => "The file is in use by another app.",
        FileNotFoundException => "The file no longer exists.",
        _ => ex.Message,
    };
}

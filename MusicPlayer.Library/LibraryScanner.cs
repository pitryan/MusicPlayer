using System.Collections.Concurrent;
using System.Diagnostics;

namespace MusicPlayer.Library;

public enum ScanStage { FindingFiles, ReadingTags, Saving }

/// <summary>Laporan kemajuan scan. Total 0 berarti jumlah totalnya belum diketahui.</summary>
public sealed record ScanProgress(ScanStage Stage, int Done, int Total)
{
    /// <summary>Nama tahapnya untuk ditampilkan, dalam bahasa Inggris seperti tampilan aplikasi lainnya.</summary>
    public string StageText => Stage switch
    {
        ScanStage.FindingFiles => "Finding files",
        ScanStage.ReadingTags => "Reading tags",
        _ => "Saving",
    };
}

public sealed record ScanResult(
    int Added, int Updated, int Removed, int Unchanged,
    IReadOnlyList<string> Errors, IReadOnlyList<string> UnavailableFolders, TimeSpan Elapsed);

/// <summary>
/// Menyamakan isi database dengan isi folder library di disk. Hanya file baru atau yang berubah
/// (ukuran atau waktu ubahnya berbeda) yang dibaca ulang tag-nya, jadi scan berikutnya jauh lebih cepat.
/// </summary>
public sealed class LibraryScanner(LibraryDatabase database)
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".wav", ".aac", ".m4a" };

    public ScanResult Scan(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Lagu yang tertinggal di luar semua folder library (sisa versi lama) tidak akan pernah terhapus oleh
        // perbandingan di bawah, jadi dibuang dulu
        int orphans = database.RemoveOrphanTracks();
        var known = database.GetFileStamps();
        // Path lagu lama ditulis persis seperti di database: kalau foldernya diganti namanya hanya huruf besar/kecil
        // non-ASCII ("élan" → "Élan"), NOCASE di SQLite menganggapnya path lain dan lagunya tersimpan ganda
        var knownPaths = new HashSet<string>(known.Keys, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Seen: stempel lagu itu di database saat scan dimulai, null untuk lagu baru (lihat UpsertScannedTracks)
        var toRead = new List<(string Path, FileStamp Stamp, FileStamp? Seen)>();
        var availableFolders = new List<string>();
        var unavailableFolders = new List<string>();
        int unchanged = 0;

        // 1. Kumpulkan semua file audio, lalu bandingkan dengan isi database
        foreach (string folder in database.GetFolders())
        {
            if (!Directory.Exists(folder))
            {
                // Misalnya hard disk eksternal sedang tidak terpasang: lagunya jangan dihapus
                unavailableFolders.Add(folder);
                continue;
            }
            availableFolders.Add(folder);

            foreach (string file in EnumerateAudioFiles(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileStamp stamp;
                try
                {
                    stamp = FileStamp.From(new FileInfo(file));
                }
                catch (IOException)
                {
                    continue; // file terhapus atau dipindah saat scan sedang berjalan
                }

                found.Add(file);
                if (!known.TryGetValue(file, out var old))
                    toRead.Add((file, stamp, null));
                else if (old != stamp)
                    toRead.Add((knownPaths.TryGetValue(file, out string? stored) ? stored : file, stamp, old));
                else
                    unchanged++;

                if (found.Count % 200 == 0)
                    progress?.Report(new ScanProgress(ScanStage.FindingFiles, found.Count, 0));
            }
        }

        // 2. Baca tag file baru dan yang berubah, beberapa file sekaligus
        var tracks = new ConcurrentBag<(Track Track, FileStamp? Seen)>();
        var errors = new ConcurrentBag<string>();
        int done = 0;

        Parallel.ForEach(
            toRead,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            item =>
            {
                var track = TagReader.Read(item.Path, item.Stamp, out string? error);
                if (error is null)
                {
                    tracks.Add((track, item.Seen));
                }
                else
                {
                    // Misalnya file sedang dikunci aplikasi lain. Lagu yang sudah ada dibiarkan dengan data dan stempel
                    // lamanya; lagu baru tetap masuk daftar, tapi dengan waktu ubah 0. Keduanya lalu dibaca ulang di
                    // scan berikutnya, tidak tersimpan kosong selamanya. Tanggal ditambahkan lagu baru tetap waktu ubah
                    // saat ditemukan, walaupun filenya berubah lagi sebelum terbaca.
                    errors.Add($"{item.Path}: {error}");
                    if (item.Seen is null)
                        tracks.Add((track with { AddedUtcTicks = track.ModifiedUtcTicks, ModifiedUtcTicks = 0 }, null));
                }

                int count = Interlocked.Increment(ref done);
                if (count % 20 == 0 || count == toRead.Count)
                    progress?.Report(new ScanProgress(ScanStage.ReadingTags, count, toRead.Count));
            });

        // 3. Simpan ke database, lalu hapus lagu yang filenya sudah tidak ada. Lagu yang tag-nya disimpan editor selama
        // scan berjalan dilewati, supaya tidak tertimpa tag lama yang dibaca scan sebelumnya.
        progress?.Report(new ScanProgress(ScanStage.Saving, 0, 0));
        var written = database.UpsertScannedTracks(tracks);

        var removed = known.Keys
            .Where(path => !found.Contains(path) && availableFolders.Any(f => LibraryPaths.IsUnder(path, f)))
            .ToList();
        database.DeleteTracks(removed);

        // Hanya lagu yang benar-benar ditulis scan ini yang dihitung "added" atau "updated" (lagu baru yang gagal dibaca
        // tersimpan sebagai baris sementara; lagu lama yang gagal dibaca tidak tersimpan). Lagu yang dilewati sudah
        // berisi data terbaru dari editor dan tidak diubah scan ini, jadi dihitung "unchanged".
        int added = written.Count(x => x.Seen is null);
        return new ScanResult(
            Added: added,
            Updated: written.Count - added,
            Removed: removed.Count + orphans,
            Unchanged: unchanged + (tracks.Count - written.Count),
            Errors: errors.ToList(),
            UnavailableFolders: unavailableFolders,
            Elapsed: stopwatch.Elapsed);
    }

    private static IEnumerable<string> EnumerateAudioFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        return Directory.EnumerateFiles(folder, "*", options)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f))
                        // "._lagu.flac" adalah file sampah buatan macOS, bukan audio
                        && !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal));
    }
}

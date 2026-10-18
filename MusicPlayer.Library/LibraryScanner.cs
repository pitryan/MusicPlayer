using System.Collections.Concurrent;
using System.Diagnostics;

namespace MusicPlayer.Library;

/// <summary>Laporan kemajuan scan. Total 0 berarti jumlah totalnya belum diketahui.</summary>
public sealed record ScanProgress(string Stage, int Done, int Total);

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
        var known = database.GetFileStamps();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toRead = new List<(string Path, FileStamp Stamp, bool IsNew)>();
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
                    toRead.Add((file, stamp, true));
                else if (old != stamp)
                    toRead.Add((file, stamp, false));
                else
                    unchanged++;

                if (found.Count % 200 == 0)
                    progress?.Report(new ScanProgress("Mencari file", found.Count, 0));
            }
        }

        // 2. Baca tag file baru dan yang berubah, beberapa file sekaligus
        var tracks = new ConcurrentBag<Track>();
        var errors = new ConcurrentBag<string>();
        int done = 0;

        Parallel.ForEach(
            toRead,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            item =>
            {
                tracks.Add(TagReader.Read(item.Path, item.Stamp, out string? error));
                if (error is not null)
                    errors.Add($"{item.Path}: {error}");

                int count = Interlocked.Increment(ref done);
                if (count % 20 == 0 || count == toRead.Count)
                    progress?.Report(new ScanProgress("Membaca tag", count, toRead.Count));
            });

        // 3. Simpan ke database, lalu hapus lagu yang filenya sudah tidak ada
        progress?.Report(new ScanProgress("Menyimpan", 0, 0));
        database.UpsertTracks(tracks);

        var removed = known.Keys
            .Where(path => !found.Contains(path) && availableFolders.Any(f => LibraryPaths.IsUnder(path, f)))
            .ToList();
        database.DeleteTracks(removed);

        int added = toRead.Count(x => x.IsNew);
        return new ScanResult(
            Added: added,
            Updated: toRead.Count - added,
            Removed: removed.Count,
            Unchanged: unchanged,
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

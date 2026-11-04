namespace MusicPlayer.Library;

/// <summary>
/// File audio yang dikenali aplikasi, dipakai bersama oleh scanner library dan lagu yang dibuka dari luar (Open with di
/// Explorer, atau file dan folder yang di-drop ke jendela).
/// </summary>
public static class AudioFiles
{
    /// <summary>Ekstensi yang didukung; sama dengan yang didaftarkan untuk "Open with" di Package.appxmanifest.</summary>
    public static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".wav", ".aac", ".m4a" };

    public static bool IsAudioFile(string path) =>
        Extensions.Contains(Path.GetExtension(path))
        // "._lagu.flac" adalah file sampah buatan macOS, bukan audio
        && !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);

    /// <summary>File audio di dalam folder, termasuk semua subfolder-nya. File tersembunyi dan file sistem dilewati.</summary>
    public static IEnumerable<string> InFolder(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        return Directory.EnumerateFiles(folder, "*", options).Where(IsAudioFile);
    }

    /// <summary>
    /// File audio dari file dan folder yang dibuka atau di-drop sekaligus, diurutkan menurut path supaya lagu satu album
    /// berurutan sesuai nomor track di nama file-nya. Folder ikut diambil isinya, termasuk subfolder; file lain dan path
    /// yang sudah tidak ada dilewati, dan file yang sama hanya sekali.
    /// </summary>
    public static List<string> Expand(IEnumerable<string> paths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
                files.UnionWith(InFolder(path));
            else if (File.Exists(path) && IsAudioFile(path))
                files.Add(Path.GetFullPath(path));
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Lagu untuk file-file ini, dengan urutan yang sama. Lagu yang ada di library memakai datanya dari sana, supaya
    /// favorit, riwayat putar, dan playlist-nya tetap tersambung. Yang tidak ada dibaca langsung dari tag-nya, tanpa
    /// ditambahkan ke library. Membaca tag bisa lama untuk banyak file, jadi panggil di luar UI thread.
    /// </summary>
    public static List<Track> ToTracks(LibraryDatabase database, IReadOnlyList<string> files)
    {
        var inLibrary = database.GetTracksByPath(files);
        var tracks = new List<Track>(files.Count);
        foreach (string file in files)
        {
            if (inLibrary.TryGetValue(file, out var track))
            {
                tracks.Add(track);
                continue;
            }

            try
            {
                tracks.Add(TagReader.Read(file, FileStamp.From(new FileInfo(file)), out _));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File-nya baru saja dipindah atau dihapus, atau tidak boleh dibaca
            }
        }
        return tracks;
    }
}

namespace MusicPlayer.Library;

public static class LibraryPaths
{
    /// <summary>
    /// Merapikan path folder: dijadikan path lengkap dan selalu diakhiri pemisah folder,
    /// supaya "D:\Musik" tidak dianggap sebagai induk dari "D:\Musik2".
    /// </summary>
    public static string NormalizeFolder(string path)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Path folder tanpa pemisah di akhir, sama seperti yang disimpan di kolom "folder" database.
    /// Root drive tetap utuh: "D:\" tetap "D:\".
    /// </summary>
    public static string WithoutTrailingSeparator(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim().Trim('"')));

    /// <summary>Nama folder untuk ditampilkan: "D:\Musik\Jepang\" menjadi "Jepang"; root drive tetap "D:\".</summary>
    public static string DisplayName(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return name.Length > 0 ? name : folder;
    }

    /// <summary>True kalau path berada di dalam folder tersebut (atau sama dengan folder itu).</summary>
    public static bool IsUnder(string path, string normalizedFolder) =>
        path.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path + Path.DirectorySeparatorChar, normalizedFolder, StringComparison.OrdinalIgnoreCase);
}

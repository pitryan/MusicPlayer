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

    /// <summary>True kalau path berada di dalam folder tersebut (atau sama dengan folder itu).</summary>
    public static bool IsUnder(string path, string normalizedFolder) =>
        path.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path + Path.DirectorySeparatorChar, normalizedFolder, StringComparison.OrdinalIgnoreCase);
}

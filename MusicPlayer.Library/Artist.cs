using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>Ringkasan satu artis di halaman Artists.</summary>
public sealed record ArtistSummary(string Name, int AlbumCount, int TrackCount, string CoverTrackPath)
{
    public string DisplayName => Name.Length > 0 ? Name : "Unknown Artist";
}

/// <summary>Isi halaman artis (lihat LibraryDatabase.GetArtistOverview).</summary>
public sealed record ArtistOverview(
    string Name,
    AlbumSummary? LatestRelease,
    IReadOnlyList<PlayCount> TopSongs,
    IReadOnlyList<AlbumSummary> Albums,
    IReadOnlyList<AlbumSummary> SinglesAndEPs,
    IReadOnlyList<AlbumSummary> AppearsOn)
{
    /// <summary>Jumlah lagu di rilis milik artis ini sendiri (tanpa Appears On).</summary>
    public int SongCount => Albums.Concat(SinglesAndEPs).Sum(a => a.TrackCount);

    public int ReleaseCount => Albums.Count + SinglesAndEPs.Count;
}

/// <summary>Aturan untuk menentukan artis utama dari sebuah nama artis.</summary>
public static class ArtistNames
{
    // Bagian "featuring" di akhir nama: " feat. X", " ft. X", " featuring X", "(feat. X)", "[feat. X]", "（feat. X）"
    private static readonly Regex FeaturingPattern = new(
        @"\s*[\(\[（]?\s*\b(feat\.?|ft\.?|featuring)\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Artis utama tanpa bagian "feat.". Contoh: "25時、ナイトコードで。 feat. 宵崎奏&amp;初音ミク" menjadi
    /// "25時、ナイトコードで。". Nama kolaborasi seperti "syudou × バルーン" sengaja tidak dipecah,
    /// karena tanda × dan &amp; juga sering menjadi bagian dari nama grup.
    /// </summary>
    public static string Primary(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        string primary = FeaturingPattern.Replace(name, "").Trim();
        return primary.Length > 0 ? primary : name.Trim();
    }

    // Daftar artis tamu setelah "feat.", di nama artis maupun di judul lagu ("Engeki (feat. A, B & C)")
    private static readonly Regex FeaturedList = new(
        @"\b(?:feat\.?|ft\.?|featuring)\s+(?<list>[^\)\]）]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Pemisah nama di daftar tamu. "、" sengaja tidak dipakai, karena ada di nama grup seperti "25時、ナイトコードで。"
    private static readonly Regex ListSeparator = new(
        @"\s*(?:,|&|＆|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Apakah artis ini tercantum di sebuah lagu: sebagai artis utama lagunya, atau sebagai tamu ("feat.") di nama
    /// artis atau judul lagu. Dipakai untuk bagian Appears On di halaman artis.
    /// </summary>
    public static bool IsCredited(Track track, string artistName)
    {
        if (artistName.Length == 0)
            return false;
        if (string.Equals(Primary(track.Artist), artistName, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string? text in new[] { track.Artist, track.Title })
        {
            if (string.IsNullOrEmpty(text))
                continue;
            foreach (Match match in FeaturedList.Matches(text))
            {
                string list = match.Groups["list"].Value.Trim();
                // Daftar utuh juga dicocokkan, untuk nama grup yang memuat "&" seperti "A & B"
                if (string.Equals(list, artistName, StringComparison.OrdinalIgnoreCase)
                    || ListSeparator.Split(list).Any(n => string.Equals(n.Trim(), artistName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }
}

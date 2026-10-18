using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>Ringkasan satu artis di halaman Artists.</summary>
public sealed record ArtistSummary(string Name, int AlbumCount, int TrackCount, string CoverTrackPath)
{
    public string DisplayName => Name.Length > 0 ? Name : "Unknown Artist";
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
}

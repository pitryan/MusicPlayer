namespace MusicPlayer.Library;

/// <summary>
/// Satu pilihan cover dari Cover Art Archive (MusicBrainz) untuk "Change Artwork": satu rilis, dengan keterangan untuk
/// membedakan edisinya, misalnya "2023 · JP · CD". Hasil pencarian MusicBrainz tidak menyebut apakah rilisnya punya
/// cover (informasi itu hanya ada saat satu rilis diambil lengkap), jadi semua rilis menjadi kandidat; yang ternyata
/// tidak punya cover (Cover Art Archive menjawab 404) disembunyikan halaman Change Artwork setelah thumbnail-nya dicoba.
/// </summary>
public sealed record ArtworkCandidate(string ReleaseId, string Title, string Artist, string Detail)
{
    /// <summary>Semua rilis, urutannya tetap (paling cocok dulu).</summary>
    public static IReadOnlyList<ArtworkCandidate> From(IEnumerable<MbRelease> releases) =>
        releases
            .Select(r => new ArtworkCandidate(
                r.Id,
                r.Title,
                string.Concat(r.Credits.Select(c => c.Name + c.JoinPhrase)),
                DetailOf(r)))
            .ToList();

    /// <summary>Tahun, negara, dan format media pertama, yang ada saja.</summary>
    public static string DetailOf(MbRelease release)
    {
        string? year = release.Date is { Length: >= 4 } date ? date[..4] : null;
        string? format = release.Media.FirstOrDefault()?.Format;
        return string.Join(" · ", new[] { year, release.Country, format }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }
}

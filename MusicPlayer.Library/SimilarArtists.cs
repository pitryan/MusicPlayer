namespace MusicPlayer.Library;

/// <summary>
/// "Similar Artists" dari library sendiri, karena data artis serupa ala Apple Music butuh layanan berbayar. Tiga
/// petunjuk, dari yang paling kuat: pernah berkolaborasi (feat.), sering didengarkan berdekatan di History, dan genre
/// yang sama.
/// </summary>
public static class SimilarArtists
{
    public const double Collaboration = 3;
    public const double ListenedTogether = 2;
    public const double SameGenre = 1;

    // Putaran dianggap "berdekatan" kalau paling jauh dua lagu dan 30 menit dari lagu artis ini
    private const int Neighbourhood = 2;
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<string> Find(string artist, IReadOnlyList<Track> library, IReadOnlyList<PlayRecord> plays, int count)
    {
        var byArtist = library
            .GroupBy(PrimaryOf, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !IsVarious(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var own = byArtist.TryGetValue(artist, out var tracks) ? tracks : [];
        var ownGenres = Genres(own);

        // Didengar berdekatan: lagu artis lain yang diputar tidak jauh sebelum atau sesudah lagu artis ini
        var ordered = plays.OrderBy(p => p.PlayedUtc).ToList();
        var together = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!SameName(PrimaryOf(ordered[i].Track), artist))
                continue;
            for (int j = Math.Max(0, i - Neighbourhood); j <= Math.Min(ordered.Count - 1, i + Neighbourhood); j++)
            {
                string other = PrimaryOf(ordered[j].Track);
                if (j == i || SameName(other, artist) || other.Length == 0
                    || (ordered[j].PlayedUtc - ordered[i].PlayedUtc).Duration() > SessionGap)
                    continue;
                together[other] = together.GetValueOrDefault(other) + 1;
            }
        }
        double mostTogether = together.Count > 0 ? together.Values.Max() : 1;

        var scored = new List<(string Name, double Score, int Tracks)>();
        foreach (var (other, otherTracks) in byArtist)
        {
            if (SameName(other, artist))
                continue;

            // Kolaborasi: artis ini dikreditkan di lagu artis lain, atau sebaliknya
            int collaborations = otherTracks.Count(t => ArtistNames.IsCredited(t, artist)) + own.Count(t => ArtistNames.IsCredited(t, other));
            double score = Collaboration * Math.Min(collaborations, 3) / 3.0
                + ListenedTogether * together.GetValueOrDefault(other) / mostTogether
                + SameGenre * Cosine(ownGenres, Genres(otherTracks));
            if (score > 0)
                scored.Add((other, score, otherTracks.Count));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Tracks)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(s => s.Name)
            .ToList();
    }

    private static string PrimaryOf(Track track) => ArtistNames.Primary(track.AlbumArtist ?? track.Artist);

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsVarious(string name) => SameName(name, "Various Artists");

    private static Dictionary<string, int> Genres(IEnumerable<Track> tracks)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.Genre))
                counts[track.Genre.Trim()] = counts.GetValueOrDefault(track.Genre.Trim()) + 1;
        }
        return counts;
    }

    /// <summary>Kemiripan dua sebaran genre (0 sampai 1): 1 kalau proporsi genrenya sama persis.</summary>
    private static double Cosine(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        double dot = a.Sum(kv => (double)kv.Value * b.GetValueOrDefault(kv.Key));
        double norm = Math.Sqrt(a.Values.Sum(v => (double)v * v)) * Math.Sqrt(b.Values.Sum(v => (double)v * v));
        return norm == 0 ? 0 : dot / norm;
    }
}

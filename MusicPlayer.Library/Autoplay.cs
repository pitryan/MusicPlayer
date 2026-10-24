namespace MusicPlayer.Library;

/// <summary>
/// Memilih lagu untuk Autoplay: saat antrean habis, pemutaran berlanjut dengan lagu yang mirip dengan lagu terakhir,
/// seperti Autoplay di Apple Music, tapi hanya dari library sendiri. Acak, tapi berbobot: lagu dari artis yang sama
/// paling besar peluangnya, lalu genre yang sama, lagu favorit, dan dekade yang sama. Lagu lain tetap punya peluang
/// kecil, supaya tidak selalu itu-itu saja.
/// </summary>
public static class AutoplayPicker
{
    public const double SameArtist = 3;
    public const double SameGenre = 2;
    public const double Favourite = 1;
    public const double SameDecade = 0.5;
    public const double Anything = 0.25;

    /// <param name="seed">Lagu terakhir (yang sedang diputar).</param>
    /// <param name="exclude">Path yang tidak boleh dipilih: lagu yang baru diputar dan yang sudah ada di antrean.</param>
    public static IReadOnlyList<Track> Pick(Track seed, IReadOnlyList<Track> library, ISet<string> exclude,
        ISet<string> favourites, int count, Random random)
    {
        string seedArtist = ArtistNames.Primary(seed.AlbumArtist ?? seed.Artist);
        var candidates = library
            .Where(t => !string.Equals(t.FilePath, seed.FilePath, StringComparison.OrdinalIgnoreCase) && !exclude.Contains(t.FilePath))
            .Select(t => (Track: t, Weight: Weight(seed, seedArtist, t, favourites)))
            .ToList();

        var picked = new List<Track>();
        while (picked.Count < count && candidates.Count > 0)
        {
            // Undian berbobot tanpa pengembalian: lagu yang sudah terpilih tidak bisa terpilih lagi
            double total = candidates.Sum(c => c.Weight);
            double roll = random.NextDouble() * total;
            int index = 0;
            for (; index < candidates.Count - 1; index++)
            {
                roll -= candidates[index].Weight;
                if (roll < 0)
                    break;
            }
            picked.Add(candidates[index].Track);
            candidates.RemoveAt(index);
        }
        return picked;
    }

    public static double Weight(Track seed, string seedArtist, Track candidate, ISet<string> favourites)
    {
        double weight = Anything;
        if (seedArtist.Length > 0
            && (string.Equals(ArtistNames.Primary(candidate.AlbumArtist ?? candidate.Artist), seedArtist, StringComparison.OrdinalIgnoreCase)
                || ArtistNames.IsCredited(candidate, seedArtist)))
            weight += SameArtist;
        if (!string.IsNullOrWhiteSpace(seed.Genre) && string.Equals(seed.Genre, candidate.Genre, StringComparison.OrdinalIgnoreCase))
            weight += SameGenre;
        if (favourites.Contains(candidate.FilePath))
            weight += Favourite;
        if (seed.Year is { } year && candidate.Year is { } other && year / 10 == other / 10)
            weight += SameDecade;
        return weight;
    }
}

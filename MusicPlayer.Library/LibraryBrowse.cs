namespace MusicPlayer.Library;

public enum BrowseSort { Default, Title, Artist, Album, Duration, Genre, Quality, ReleaseYear, DateAdded }

/// <summary>Pilihan tampilan saja; tidak mengubah urutan playlist yang disimpan.</summary>
public sealed record BrowseOptions(BrowseSort Sort = BrowseSort.Default, bool Descending = false, bool OnlyFavourites = false);

public static class LibraryBrowse
{
    private static readonly StringComparer Names = StringComparer.CurrentCultureIgnoreCase;

    public static IEnumerable<T> Tracks<T>(IEnumerable<T> source, Func<T, Track> trackOf,
        BrowseOptions options, Func<Track, bool> isFavourite)
    {
        var filtered = options.OnlyFavourites ? source.Where(x => isFavourite(trackOf(x))) : source;
        if (options.Sort == BrowseSort.Default)
            return filtered;

        return filtered.OrderBy(trackOf, Comparer<Track>.Create((a, b) =>
        {
            int primary = options.Sort switch
            {
                BrowseSort.Title => Names.Compare(a.DisplayTitle, b.DisplayTitle),
                BrowseSort.Artist => Names.Compare(a.DisplayArtist, b.DisplayArtist),
                BrowseSort.Album => Names.Compare(a.DisplayAlbum, b.DisplayAlbum),
                BrowseSort.Duration => a.Duration.CompareTo(b.Duration),
                BrowseSort.Genre => Names.Compare(a.Genre, b.Genre),
                BrowseSort.Quality => CompareQuality(a, b),
                _ => 0,
            };
            if (primary != 0)
                return options.Descending ? -Math.Sign(primary) : primary;

            // Arah urutan album/artis tidak membalik nomor disc dan track di dalam album.
            if (options.Sort is BrowseSort.Album or BrowseSort.Artist)
            {
                int album = Names.Compare(a.AlbumArtist ?? a.Artist, b.AlbumArtist ?? b.Artist);
                if (album == 0) album = Names.Compare(a.Album, b.Album);
                if (album != 0) return options.Descending ? -Math.Sign(album) : album;
                int disc = (a.DiscNumber ?? 1).CompareTo(b.DiscNumber ?? 1);
                if (disc != 0) return disc;
                int track = (a.TrackNumber ?? 0).CompareTo(b.TrackNumber ?? 0);
                if (track != 0) return track;
            }
            return Names.Compare(a.DisplayTitle, b.DisplayTitle);
        }));
    }

    private static int CompareQuality(Track a, Track b)
    {
        int value = AudioQuality.Describe(a).Kind.CompareTo(AudioQuality.Describe(b).Kind);
        if (value == 0) value = a.SampleRate.CompareTo(b.SampleRate);
        if (value == 0) value = a.BitsPerSample.CompareTo(b.BitsPerSample);
        if (value == 0) value = a.BitrateKbps.CompareTo(b.BitrateKbps);
        return value;
    }

    public static IEnumerable<AlbumSummary> Albums(IEnumerable<AlbumSummary> source, BrowseOptions options,
        IEnumerable<Track> favourites)
    {
        if (options.OnlyFavourites)
        {
            var keys = favourites.Select(t => AlbumKey(t.AlbumArtist ?? t.Artist ?? "", t.Album ?? ""))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            source = source.Where(a => keys.Contains(AlbumKey(a.Artist, a.Title)));
        }
        return options.Sort switch
        {
            BrowseSort.Title => Order(source, a => a.Title, options.Descending, Names),
            BrowseSort.Artist => Order(source, a => a.DisplayArtist, options.Descending, Names).ThenBy(a => a.Title, Names),
            BrowseSort.ReleaseYear => Order(source, a => a.Year ?? 0, options.Descending).ThenBy(a => a.Title, Names),
            BrowseSort.DateAdded => Order(source, a => a.AddedUtc, options.Descending).ThenBy(a => a.Title, Names),
            _ => source,
        };
    }

    private static string AlbumKey(string artist, string album) => artist + "\0" + album;

    private static IOrderedEnumerable<T> Order<T, TKey>(IEnumerable<T> source, Func<T, TKey> key,
        bool descending, IComparer<TKey>? comparer = null) =>
        descending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    public static IEnumerable<ArtistSummary> Artists(IEnumerable<ArtistSummary> source, BrowseOptions options,
        IEnumerable<Track> favourites)
    {
        if (options.OnlyFavourites)
        {
            var names = favourites.Select(t => ArtistNames.Primary(t.AlbumArtist ?? t.Artist))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            source = source.Where(a => names.Contains(a.Name));
        }
        return Order(source, a => a.DisplayName, options.Descending, Names);
    }

    /// <summary>Artis utama lagu-lagu playlist, terbanyak dulu; hanya memakai kredit yang ada di tag.</summary>
    public static IReadOnlyList<ArtistSummary> FeaturedArtists(IEnumerable<Track> tracks) => tracks
        .DistinctBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
        .GroupBy(t => ArtistNames.Primary(t.Artist ?? t.AlbumArtist), StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Key.Length > 0)
        .Select(g => new ArtistSummary(g.Key,
            g.Select(t => AlbumKey(t.AlbumArtist ?? t.Artist ?? "", t.Album ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            g.Count(), g.First().FilePath))
        .OrderByDescending(a => a.TrackCount).ThenBy(a => a.DisplayName, Names).ToList();
}

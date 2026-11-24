namespace MusicPlayer.Library.Tests;

public sealed class LibraryBrowseTests
{
    private static Track Song(string title, string album = "Album", int disc = 1, int number = 1) => new()
    {
        FilePath = title + ".flac", FolderPath = "Music", Title = title, Artist = "Artist", Album = album,
        DiscNumber = disc, TrackNumber = number, Codec = "FLAC", BitsPerSample = 16, SampleRate = 44100,
    };

    [Fact]
    public void SortingAndFiltering_PreservePlaylistEntriesAndManualOrder()
    {
        var a = Song("A");
        var b = Song("B");
        var entries = new[] { (Id: 1, Track: b), (Id: 2, Track: a), (Id: 3, Track: b) };

        var sorted = LibraryBrowse.Tracks(entries, e => e.Track, new(BrowseSort.Title), _ => false).ToList();
        Assert.Equal([2, 1, 3], sorted.Select(e => e.Id));
        var filtered = LibraryBrowse.Tracks(entries, e => e.Track, new(BrowseSort.Title, OnlyFavourites: true), t => t == b);
        Assert.Equal([1, 3], filtered.Select(e => e.Id));
        var restored = LibraryBrowse.Tracks(entries, e => e.Track, new(), _ => false);
        Assert.Equal([1, 2, 3], restored.Select(e => e.Id));
    }

    [Fact]
    public void AlbumDescending_KeepsDiscAndTrackNumbersAscending()
    {
        var tracks = new[] { Song("Disc 2", "Z", 2, 1), Song("First", "Z", 1, 1),
            Song("Other", "A"), Song("Second", "Z", 1, 2) };
        var sorted = LibraryBrowse.Tracks(tracks, t => t, new(BrowseSort.Album, Descending: true), _ => false);
        Assert.Equal(["First", "Second", "Disc 2", "Other"], sorted.Select(t => t.Title));
    }

    [Fact]
    public void QualitySort_UsesNumericResolutionAndCodecKind()
    {
        var high = Song("High") with { SampleRate = 192000, BitsPerSample = 24 };
        var low = Song("Lossy") with { Codec = "MP3", BitrateKbps = 320, BitsPerSample = 0 };
        var mid = Song("Mid") with { SampleRate = 96000, BitsPerSample = 24 };
        var cd = Song("CD");
        var sorted = LibraryBrowse.Tracks(new[] { high, low, mid, cd }, t => t, new(BrowseSort.Quality), _ => false);
        Assert.Equal([low, cd, mid, high], sorted);
    }

    [Fact]
    public void FavouriteAlbumFilter_DistinguishesSameTitleByAlbumArtist()
    {
        var albums = new[]
        {
            new AlbumSummary("Live", "A", 2024, 1, TimeSpan.Zero, null, "a.flac", DateTime.UtcNow),
            new AlbumSummary("Live", "B", 2024, 1, TimeSpan.Zero, null, "b.flac", DateTime.UtcNow),
        };
        var favourite = Song("Solo", "Live") with { AlbumArtist = "b", Artist = "Guest" };
        Assert.Equal([albums[1]], LibraryBrowse.Albums(albums, new(OnlyFavourites: true), [favourite]));
    }

    [Fact]
    public void FavouriteArtistFilter_UsesSamePrimaryArtistAsLibrary()
    {
        var artists = new[] { new ArtistSummary("Group", 1, 2, "a.flac"), new ArtistSummary("Other", 1, 1, "b.flac") };
        var favourite = Song("A") with { AlbumArtist = "Group feat. Guest" };
        Assert.Equal([artists[0]], LibraryBrowse.Artists(artists, new(OnlyFavourites: true), [favourite]));
    }

    [Fact]
    public void FeaturedArtists_GroupPrimaryCreditsAndDoNotCountDuplicateFilesTwice()
    {
        var a = Song("A") with { Artist = "Group feat. Guest" };
        var b = Song("B") with { Artist = "Group" };
        var c = Song("C") with { Artist = "Other", AlbumArtist = "Various Artists" };
        var unknown = Song("Unknown") with { Artist = null, AlbumArtist = null };
        var result = LibraryBrowse.FeaturedArtists([a, a, b, c, unknown]);
        Assert.Equal(["Group", "Other"], result.Select(x => x.Name));
        Assert.Equal([2, 1], result.Select(x => x.TrackCount));
        Assert.Equal(a.FilePath, result[0].CoverTrackPath);
    }
}

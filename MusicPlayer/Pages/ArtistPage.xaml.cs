using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Foundation;

namespace MusicPlayer.Pages;

/// <summary>Halaman artis. Parameter navigasinya nama artis utama (seperti di halaman Artists, tanpa "feat.").</summary>
public sealed partial class ArtistPage : Page
{
    // Top Songs cukup lima, supaya tingginya sejajar dengan kotak Latest Release di sebelahnya
    private const int TopSongCount = 5;
    private const int PhotoPixelWidth = 360;
    private const int LatestCoverPixelWidth = 400;

    private ArtistOverview? _overview;
    private List<Track> _topTracks = [];
    private List<TrackRow> _topRows = [];

    public ArtistPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load(e.Parameter as string ?? "");

        App.Playback.PropertyChanged += Playback_PropertyChanged;
        App.Favourites.Changed += Favourites_Changed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Playback.PropertyChanged -= Playback_PropertyChanged;
        App.Favourites.Changed -= Favourites_Changed;
    }

    private void Load(string name)
    {
        var overview = App.Database.GetArtistOverview(name, TopSongCount);
        _overview = overview;

        NameText.Text = name.Length > 0 ? name : "Unknown Artist";
        MetaText.Text = overview.ReleaseCount > 0
            ? $"{Formatters.Count(overview.ReleaseCount, "release")} · {Formatters.Count(overview.SongCount, "song")}"
            : $"Appears on {Formatters.Count(overview.AppearsOn.Count, "album")}";
        PlayButton.IsEnabled = overview.SongCount > 0;
        ShuffleButton.IsEnabled = overview.SongCount > 0;

        ShowLatestRelease(overview.LatestRelease);
        ShowTopSongs(overview.TopSongs);
        HighlightsRow.Visibility = overview.LatestRelease is not null || overview.TopSongs.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Rilis sendiri diberi tahun di bawah judulnya (artisnya sudah jelas); Appears On diberi nama artis albumnya
        ShowShelf(AlbumsTitle, AlbumsGrid, overview.Albums.Select(a => new AlbumItem(a) { Subtitle = a.Year?.ToString() ?? "" }));
        ShowShelf(SinglesTitle, SinglesGrid, overview.SinglesAndEPs.Select(a => new AlbumItem(a)
        {
            Subtitle = a.Year is { } year ? $"{year} · {Formatters.ReleaseKindLabel(a.Kind)}" : Formatters.ReleaseKindLabel(a.Kind),
        }));
        ShowShelf(AppearsOnTitle, AppearsOnGrid, overview.AppearsOn.Select(a => new AlbumItem(a)));

        _ = LoadImagesAsync(overview);
    }

    private void ShowLatestRelease(AlbumSummary? latest)
    {
        LatestPanel.Visibility = latest is null ? Visibility.Collapsed : Visibility.Visible;
        if (latest is null)
            return;

        LatestDateText.Text = Formatters.ReleaseDate(latest.ReleaseDate, latest.Year).ToUpperInvariant();
        LatestTitleText.Text = latest.Title;
        LatestInfoText.Text = $"{Formatters.ReleaseKindLabel(latest.Kind)} · {Formatters.Count(latest.TrackCount, "song")}";
    }

    private void ShowTopSongs(IReadOnlyList<PlayCount> topSongs)
    {
        _topTracks = topSongs.Select(c => c.Track).ToList();
        _topRows = topSongs.Select((c, index) => new TrackRow(c.Track, index)).ToList();
        TopSongsList.ItemsSource = _topRows;
        // Top Songs dihitung dari History, jadi artis yang lagunya belum pernah diputar tidak punya bagian ini
        TopSongsPanel.Visibility = _topRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkNowPlaying();
    }

    private static void ShowShelf(TextBlock title, GridView grid, IEnumerable<AlbumItem> items)
    {
        var list = items.ToList();
        grid.ItemsSource = list;
        var visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        title.Visibility = visibility;
        grid.Visibility = visibility;
    }

    /// <summary>Foto artis = cover rilis terbaru (atau album Appears On kalau tidak punya rilis sendiri).</summary>
    private async Task LoadImagesAsync(ArtistOverview overview)
    {
        var photoAlbum = overview.LatestRelease ?? overview.AppearsOn.FirstOrDefault();
        var photo = photoAlbum is null ? null : await CoverCache.GetAsync(photoAlbum.CoverTrackPath, PhotoPixelWidth);
        var latest = overview.LatestRelease is null ? null : await CoverCache.GetAsync(overview.LatestRelease.CoverTrackPath, LatestCoverPixelWidth);

        // Halaman sudah pindah ke artis lain selama cover dimuat
        if (!ReferenceEquals(overview, _overview))
            return;

        PhotoBrush.ImageSource = photo;
        LatestCoverBrush.ImageSource = latest;
    }

    private void Favourites_Changed(object? sender, EventArgs e) => TrackRow.RefreshFavourites(_topRows);

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
            MarkNowPlaying();
    }

    private void MarkNowPlaying()
    {
        string? playingPath = App.Playback.CurrentTrack?.FilePath;
        foreach (var row in _topRows)
            row.IsPlaying = row.Track.FilePath == playingPath;
    }

    /// <summary>Semua lagu rilis artis ini sendiri, dari rilis terbaru, dengan urutan lagu setiap albumnya.</summary>
    private List<Track> OwnTracks()
    {
        if (_overview is null)
            return [];

        return _overview.Albums.Concat(_overview.SinglesAndEPs)
            .OrderByDescending(a => a.ReleaseSortKey, StringComparer.Ordinal)
            .SelectMany(a => App.Database.GetAlbumTracks(a.Artist, a.Title))
            .ToList();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var tracks = OwnTracks();
        if (tracks.Count > 0)
            App.Playback.PlayQueue(tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        var tracks = OwnTracks();
        if (tracks.Count > 0)
            App.Playback.PlayShuffled(tracks);
    }

    private void Latest_Click(object sender, RoutedEventArgs e)
    {
        if (_overview?.LatestRelease is { } latest)
            Frame.Navigate(typeof(AlbumDetailPage), latest, Animations.DrillIn);
    }

    private void Release_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItem album)
            Frame.Navigate(typeof(AlbumDetailPage), album.Summary, Animations.DrillIn);
    }

    private void Release_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is AlbumItem album)
            album.EnsureCover();
    }

    private void TopSongsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is TrackRow row)
            row.EnsureCover();
    }

    private void TopSongMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackRow row } button)
            TrackMenu.Show(button, new Point(0, button.ActualHeight), _topTracks, _topRows.IndexOf(row), goToArtist: false);
    }

    private void TopSongsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
            TrackMenu.Show(TopSongsList, e.GetPosition(TopSongsList), _topTracks, _topRows.IndexOf(row), goToArtist: false);
    }

    private void TopSongsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan Top Songs sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
        {
            int index = _topRows.IndexOf(row);
            if (index >= 0)
                App.Playback.PlayQueue(_topTracks, index);
        }
    }

    // Kotak membesar sedikit saat kursor di atasnya, seperti Apple Music
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);
}

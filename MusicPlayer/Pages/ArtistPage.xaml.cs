using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
    // Top Songs: lima kolom berisi tiga lagu, digulir mendatar seperti Apple Music
    private const int TopSongCount = 15;
    // Banner selebar halaman: cover dimuat besar supaya tetap tajam
    private const int BannerPixelWidth = 1280;
    private const int LatestCoverPixelWidth = 400;
    private const double TopSongColumns = 3;
    private const int SimilarArtistCount = 10;
    // Teks About di atas panjang ini dipotong 8 baris, dengan tombol More
    private const int AboutCollapsedLength = 500;

    private ArtistOverview? _overview;
    private List<Track> _topTracks = [];
    private List<TrackRow> _topRows = [];
    private CancellationTokenSource? _infoCts;
    private bool _aboutExpanded;

    public ArtistPage()
    {
        InitializeComponent();
        ShelfScroll.Attach(TopSongsScroller, TopSongsRepeater, TopSongsLeft, TopSongsRight);
        ShelfScroll.Attach(SimilarScroller, SimilarRepeater, SimilarLeft, SimilarRight);
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
        _infoCts?.Cancel();
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
        AlbumsShelf.Items = overview.Albums.Select(a => new AlbumItem(a) { Subtitle = a.Year?.ToString() ?? "" }).ToList();
        SinglesShelf.Items = overview.SinglesAndEPs.Select(a => new AlbumItem(a)
        {
            Subtitle = a.Year is { } year ? $"{year} · {Formatters.ReleaseKindLabel(a.Kind)}" : Formatters.ReleaseKindLabel(a.Kind),
        }).ToList();
        AppearsOnShelf.Items = overview.AppearsOn.Select(a => new AlbumItem(a)).ToList();

        _ = LoadImagesAsync(overview);

        // About: yang tersimpan langsung tampil, lalu diperbarui dari internet di latar belakang kalau sudah lama
        ShowAbout(name, App.ArtistInfo.Cached(name), overview);
        _ = RefreshArtistInfoAsync(name, overview);
        _ = LoadSimilarAsync(name, overview);
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
        TopSongsRepeater.ItemsSource = _topRows;
        foreach (var row in _topRows)
            row.EnsureCover();
        TopSongsScroller.ChangeView(0, null, null, true);
        // Top Songs dihitung dari History, jadi artis yang lagunya belum pernah diputar tidak punya bagian ini
        TopSongsPanel.Visibility = _topRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkNowPlaying();
    }

    /// <summary>Foto artis = cover rilis terbaru (atau album Appears On kalau tidak punya rilis sendiri).</summary>
    private async Task LoadImagesAsync(ArtistOverview overview)
    {
        var photoAlbum = overview.LatestRelease ?? overview.AppearsOn.FirstOrDefault();
        var photo = photoAlbum is null ? null : await CoverCache.GetAsync(photoAlbum.CoverTrackPath, BannerPixelWidth);
        var latest = overview.LatestRelease is null ? null : await CoverCache.GetAsync(overview.LatestRelease.CoverTrackPath, LatestCoverPixelWidth);

        // Halaman sudah pindah ke artis lain selama cover dimuat
        if (!ReferenceEquals(overview, _overview))
            return;

        BannerImage.Source = photo;
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

    // Kotak membesar sedikit saat kursor di atasnya, seperti Apple Music
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);

    private void Release_ItemInvoked(object? sender, AlbumItem item) =>
        Frame.Navigate(typeof(AlbumDetailPage), item.Summary, Animations.DrillIn);

    /// <summary>Menu artis: semua lagu rilisnya sendiri (Play Next, Add to Queue, playlist, info).</summary>
    private void ArtistMore_Click(object sender, RoutedEventArgs e)
    {
        var tracks = OwnTracks();
        if (tracks.Count > 0)
            TrackMenu.ShowForAlbum(MoreButton, new Point(0, MoreButton.ActualHeight), tracks);
    }

    // Banner selebar halaman dengan tinggi sekitar sepertiga lebarnya, seperti Apple Music
    private void Banner_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double height = Math.Clamp(e.NewSize.Width * 0.34, 280, 560);
        if (Math.Abs(Banner.Height - height) > 1)
            Banner.Height = height;
    }

    // Tiga kolom Top Songs mengisi lebar yang terlihat; kolom berikutnya muncul setelah digulir
    private void TopSongsScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double spacing = TopSongsLayout.MinColumnSpacing;
        TopSongsLayout.MinItemWidth = Math.Max(220, Math.Floor((e.NewSize.Width - spacing * (TopSongColumns - 1)) / TopSongColumns));
    }

    // Item dibawa lewat Tag: ItemsRepeater tidak mengisi DataContext untuk template x:Bind
    private void TopSong_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan Top Songs sebagai antrean
        if ((sender as FrameworkElement)?.Tag is TrackRow row && _topRows.IndexOf(row) is int index and >= 0)
            App.Playback.PlayQueue(_topTracks, index);
    }

    private void TopSong_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrackRow row } element)
            TrackMenu.Show(element, e.GetPosition(element), _topTracks, _topRows.IndexOf(row), goToArtist: false);
    }

    private void TopSongMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrackRow row } button)
            TrackMenu.Show(button, new Point(0, button.ActualHeight), _topTracks, _topRows.IndexOf(row), goToArtist: false);
    }

    private async Task RefreshArtistInfoAsync(string name, ArtistOverview overview)
    {
        _infoCts?.Cancel();
        var cts = _infoCts = new CancellationTokenSource();
        var info = await App.ArtistInfo.GetAsync(name, cts.Token);
        if (cts.IsCancellationRequested || !ReferenceEquals(overview, _overview))
            return;
        ShowAbout(name, info, overview);
    }

    private void ShowAbout(string name, ArtistInfo? info, ArtistOverview overview)
    {
        AboutSection.Visibility = info is { HasDetails: true } ? Visibility.Visible : Visibility.Collapsed;
        if (info is not { HasDetails: true })
            return;

        AboutTitle.Text = $"About {name}";
        AboutText.Text = info.About ?? "";
        AboutText.Visibility = string.IsNullOrWhiteSpace(info.About) ? Visibility.Collapsed : Visibility.Visible;
        _aboutExpanded = false;
        AboutText.MaxLines = 8;
        AboutMoreButton.Content = "More";
        AboutMoreButton.Visibility = (info.About?.Length ?? 0) > AboutCollapsedLength ? Visibility.Visible : Visibility.Collapsed;
        AboutSourceLink.Visibility = info.WikipediaUrl is null ? Visibility.Collapsed : Visibility.Visible;
        if (info.WikipediaUrl is { } url)
            AboutSourceLink.NavigateUri = new Uri(url);

        ShowDetail(FromPanel, FromText, info.Origin);
        BornLabel.Text = info.IsGroup ? "FORMED" : "BORN";
        ShowDetail(BornPanel, BornText, info.BeginDate is { Length: > 0 } date ? Formatters.ReleaseDate(date, null) : null);
        ShowDetail(GenrePanel, GenreText, MainGenre(overview));
    }

    private static void ShowDetail(FrameworkElement panel, TextBlock text, string? value)
    {
        text.Text = value ?? "";
        panel.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Genre yang paling sering di rilis artis ini sendiri.</summary>
    private static string? MainGenre(ArtistOverview overview) =>
        overview.Albums.Concat(overview.SinglesAndEPs)
            .Select(a => a.Genre)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    private void AboutMore_Click(object sender, RoutedEventArgs e)
    {
        _aboutExpanded = !_aboutExpanded;
        AboutText.MaxLines = _aboutExpanded ? 0 : 8;
        AboutMoreButton.Content = _aboutExpanded ? "Less" : "More";
    }

    /// <summary>Similar Artists dari library sendiri; dihitung di latar belakang karena membaca semua lagu dan History.</summary>
    private async Task LoadSimilarAsync(string name, ArtistOverview overview)
    {
        var database = App.Database;
        var (names, artists) = await Task.Run(() => (
            SimilarArtists.Find(name, database.GetTracks(), database.GetRecentPlays(5000), SimilarArtistCount),
            database.GetArtists()));
        if (!ReferenceEquals(overview, _overview))
            return;

        var byName = artists.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var items = names
            .Where(byName.ContainsKey)
            .Select(n => new ArtistItem(byName[n]))
            .ToList();
        SimilarRepeater.ItemsSource = items;
        foreach (var item in items)
            item.EnsureCover();
        SimilarSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SimilarScroller.ChangeView(0, null, null, true);
    }

    // Item dibawa lewat Tag: ItemsRepeater tidak mengisi DataContext untuk template x:Bind
    private void SimilarArtist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ArtistItem artist)
            Frame.Navigate(typeof(ArtistPage), artist.Summary.Name, Animations.DrillIn);
    }
}

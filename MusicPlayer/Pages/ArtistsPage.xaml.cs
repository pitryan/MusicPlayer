using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.ViewModels;
using Windows.Foundation;

namespace MusicPlayer.Pages;

/// <summary>
/// Halaman Artists. Parameter navigasi (opsional) adalah nama artis yang langsung dipilih,
/// misalnya saat dibuka dari hasil pencarian.
/// </summary>
public sealed partial class ArtistsPage : Page
{
    // Artis yang terakhir dipilih (nama artis utama)
    private const string SelectedArtistSetting = "ArtistsSelected";

    private List<ArtistItem> _artists = [];
    private List<AlbumBlock> _blocks = [];

    public ArtistsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.Playback.PropertyChanged += Playback_PropertyChanged;
        App.Favourites.Changed += Favourites_Changed;

        // Kembali dari halaman album: artis yang dipilih dan posisi scroll dipertahankan. Favorit bisa saja berubah
        // selama di halaman lain, jadi bintangnya disamakan lagi.
        if (e.NavigationMode == NavigationMode.Back && _artists.Count > 0 && _libraryVersion == App.TagEdits.Version)
        {
            MarkNowPlaying();
            TrackRow.RefreshFavourites(_blocks.SelectMany(b => b.Rows));
            return;
        }

        // Tag lagu diedit selama di halaman lain: daftar artis dan albumnya dimuat ulang
        _libraryVersion = App.TagEdits.Version;
        _artists = App.Database.GetArtists().Select(a => new ArtistItem(a)).ToList();
        ArtistList.ItemsSource = _artists;
        EmptyState.Visibility = _artists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Artis yang diminta (Go to Artist), atau artis terakhir yang dipilih: membuka Artists dari sidebar kembali ke
        // artis yang tadi dilihat, bukan ke artis paling atas. Diingat juga setelah aplikasi ditutup.
        string? requested = e.Parameter as string ?? AppSettings.Get(SelectedArtistSetting, "");
        var selected = _artists.FirstOrDefault(a =>
                           requested.Length > 0
                           && string.Equals(a.Summary.Name, requested, StringComparison.OrdinalIgnoreCase))
                       ?? _artists.FirstOrDefault();

        if (selected is not null)
        {
            ArtistList.SelectedItem = selected; // memicu ArtistList_SelectionChanged
            ArtistList.ScrollIntoView(selected);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Playback.PropertyChanged -= Playback_PropertyChanged;
        App.Favourites.Changed -= Favourites_Changed;
    }

    // Favorit diubah dari tempat lain (LCD, menu, halaman lain): bintang di baris ikut berubah
    private void Favourites_Changed(object? sender, EventArgs e) =>
        TrackRow.RefreshFavourites(_blocks.SelectMany(b => b.Rows));

    private void ArtistList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ArtistItem artist)
            artist.EnsureCover();
    }

    private void ArtistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtistList.SelectedItem is ArtistItem artist)
        {
            AppSettings.Set(SelectedArtistSetting, artist.Summary.Name);
            ShowArtist(artist);
        }
    }

    // Artis yang sedang tampil di kanan, untuk tombol nama artis
    private string _shownArtist = "";
    private int _libraryVersion;

    private void ArtistName_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ArtistPage), _shownArtist, Animations.DrillIn);

    private void ShowArtist(ArtistItem artist)
    {
        var albums = App.Database.GetArtistAlbums(artist.Summary.Name);
        _blocks = albums
            .Select(album => new AlbumBlock(album, App.Database.GetAlbumTracks(album.Artist, album.Title)))
            .ToList();

        ArtistNameText.Text = artist.Name;
        _shownArtist = artist.Summary.Name; // nama kunci (kosong untuk Unknown Artist), sama dengan halaman artis
        ArtistCountText.Text =
            $"{Formatters.Count(_blocks.Count, "album")}, {Formatters.Count(_blocks.Sum(b => b.Tracks.Count), "song")}";
        AlbumRepeater.ItemsSource = _blocks;

        // Satu artis biasanya hanya punya beberapa album, jadi semua cover-nya langsung dimuat
        foreach (var block in _blocks)
            block.EnsureCover();

        MarkNowPlaying();
        DetailScroller.ChangeView(null, 0, null, true); // kembali ke atas
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var tracks = _blocks.SelectMany(b => b.Tracks).ToList();
        if (tracks.Count > 0)
            App.Playback.PlayQueue(tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        App.Playback.PlayShuffled(_blocks.SelectMany(b => b.Tracks).ToList());
    }

    private void AlbumRepeater_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali sebuah lagu: putar lagu itu, dengan isi albumnya sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TrackRow row)
            return;

        var block = _blocks.FirstOrDefault(b => b.Rows.Contains(row));
        if (block is not null)
            App.Playback.PlayQueue(block.Tracks, block.Rows.IndexOf(row));
    }

    private void AlbumRepeater_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TrackRow row)
            return;

        var block = _blocks.FirstOrDefault(b => b.Rows.Contains(row));
        if (block is not null)
            TrackMenu.Show(AlbumRepeater, e.GetPosition(AlbumRepeater), block.Tracks, block.Rows.IndexOf(row), goToArtist: false);
    }

    /// <summary>Menu artis: semua lagu artis ini (Play Next, Add to Queue, playlist, info, dan halaman artisnya).</summary>
    private void ArtistMore_Click(object sender, RoutedEventArgs e)
    {
        var tracks = _blocks.SelectMany(b => b.Tracks).ToList();
        if (tracks.Count > 0 && sender is FrameworkElement button)
            TrackMenu.ShowForAlbum(button, new Point(0, button.ActualHeight), tracks);
    }

    private void AlbumMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AlbumBlock block } button)
            TrackMenu.ShowForAlbum(button, new Point(0, button.ActualHeight), block.Tracks);
    }

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TrackRow row } button)
            return;
        var block = _blocks.FirstOrDefault(b => b.Rows.Contains(row));
        if (block is not null)
            TrackMenu.Show(button, new Point(0, button.ActualHeight), block.Tracks, block.Rows.IndexOf(row), goToArtist: false);
    }

    // Cover album membesar sedikit saat kursor di atasnya
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);

    private void AlbumCover_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumBlock block)
            Frame.Navigate(typeof(AlbumDetailPage), block.Summary, Animations.DrillIn);
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.PlaybackService.CurrentTrack))
            MarkNowPlaying();
    }

    private void MarkNowPlaying()
    {
        string? playingPath = App.Playback.CurrentTrack?.FilePath;
        foreach (var row in _blocks.SelectMany(b => b.Rows))
            row.IsPlaying = row.Track.FilePath == playingPath;
    }
}

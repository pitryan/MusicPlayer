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

namespace MusicPlayer.Pages;

/// <summary>
/// Halaman Artists. Parameter navigasi (opsional) adalah nama artis yang langsung dipilih,
/// misalnya saat dibuka dari hasil pencarian.
/// </summary>
public sealed partial class ArtistsPage : Page
{
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

        // Kembali dari halaman album: artis yang dipilih dan posisi scroll dipertahankan
        if (e.NavigationMode == NavigationMode.Back && _artists.Count > 0)
        {
            MarkNowPlaying();
            return;
        }

        _artists = App.Database.GetArtists().Select(a => new ArtistItem(a)).ToList();
        ArtistList.ItemsSource = _artists;
        EmptyState.Visibility = _artists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        string? requested = e.Parameter as string;
        var selected = _artists.FirstOrDefault(a =>
                           requested is not null
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
    }

    private void ArtistList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ArtistItem artist)
            artist.EnsureCover();
    }

    private void ArtistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtistList.SelectedItem is ArtistItem artist)
            ShowArtist(artist);
    }

    private void ShowArtist(ArtistItem artist)
    {
        var albums = App.Database.GetArtistAlbums(artist.Summary.Name);
        _blocks = albums
            .Select(album => new AlbumBlock(album, App.Database.GetAlbumTracks(album.Artist, album.Title)))
            .ToList();

        ArtistNameText.Text = artist.Name;
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
        var tracks = _blocks.SelectMany(b => b.Tracks).OrderBy(_ => Random.Shared.Next()).ToList();
        if (tracks.Count > 0)
            App.Playback.PlayQueue(tracks, 0);
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

    private void AlbumCover_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumBlock block)
            Frame.Navigate(typeof(AlbumDetailPage), block.Summary);
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

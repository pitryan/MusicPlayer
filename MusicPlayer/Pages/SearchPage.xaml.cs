using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Hasil pencarian dari kotak Search di sidebar. Parameter navigasinya adalah teks yang dicari.</summary>
public sealed partial class SearchPage : Page
{
    private const int MaxArtists = 12;
    private const int MaxAlbums = 12;
    private const int MaxSongs = 50;

    private List<Track> _songs = [];

    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string query)
            ShowResults(query);
    }

    /// <summary>Dipanggil juga oleh MainWindow saat teks pencarian berubah dan halaman ini sudah terbuka.</summary>
    public void ShowResults(string query)
    {
        HeaderText.Text = $"Results for \u201C{query}\u201D";

        var artists = App.Database.GetArtists()
            .Where(a => Matches(a.DisplayName, query))
            .Take(MaxArtists)
            .Select(a => new ArtistItem(a))
            .ToList();

        var albums = App.Database.GetAlbums()
            .Where(a => Matches(a.Title, query) || Matches(a.DisplayArtist, query))
            .Take(MaxAlbums)
            .Select(a => new AlbumItem(a))
            .ToList();

        _songs = App.Database.Search(query, MaxSongs).ToList();

        ArtistResults.ItemsSource = artists;
        AlbumResults.ItemsSource = albums;
        SongResults.ItemsSource = _songs;

        // Hasilnya sedikit, jadi semua foto dan cover langsung dimuat
        foreach (var artist in artists)
            artist.EnsureCover();
        foreach (var album in albums)
            album.EnsureCover();

        ArtistsSection.Visibility = artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumsSection.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SongsSection.Visibility = _songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Visibility = artists.Count + albums.Count + _songs.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void ArtistResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistItem artist)
            Frame.Navigate(typeof(ArtistPage), artist.Summary.Name);
    }

    private void AlbumResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItem album)
            Frame.Navigate(typeof(AlbumDetailPage), album.Summary);
    }

    private void SongResults_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
            TrackMenu.Show(SongResults, e.GetPosition(SongResults), _songs, _songs.IndexOf(track));
    }

    private void SongResults_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan semua lagu hasil pencarian sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
        {
            int index = _songs.IndexOf(track);
            if (index >= 0)
                App.Playback.PlayQueue(_songs, index);
        }
    }
}

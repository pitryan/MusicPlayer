using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

public sealed partial class SongsPage : Page
{
    private List<Track> _tracks = [];
    private List<TrackRow> _rows = [];

    public SongsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _tracks = App.Database.GetTracks().ToList();
        _rows = _tracks.Select((track, index) => new TrackRow(track, index)).ToList();
        SongsList.ItemsSource = _rows;
        Header.Detail = $"{_tracks.Count} songs";
        EmptyState.Visibility = _tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        App.Favourites.Changed += Favourites_Changed;

        // Tandai lagu yang sedang diputar, dan ikuti terus saat lagunya berganti
        App.Playback.PropertyChanged += Playback_PropertyChanged;
        MarkNowPlaying();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Favourites.Changed -= Favourites_Changed;
        App.Playback.PropertyChanged -= Playback_PropertyChanged;
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
            MarkNowPlaying();
    }

    private void MarkNowPlaying()
    {
        string? playingPath = App.Playback.CurrentTrack?.FilePath;
        foreach (var row in _rows)
            row.IsPlaying = row.Track.FilePath == playingPath;
    }

    // Favorit diubah dari tempat lain (LCD, menu, halaman lain): bintang di baris ikut berubah
    private void Favourites_Changed(object? sender, EventArgs e) => TrackRow.RefreshFavourites(_rows);

    private void SongsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
            TrackMenu.Show(SongsList, e.GetPosition(SongsList), _tracks, _rows.IndexOf(row));
    }

    private void SongsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan seluruh daftar ini sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
        {
            int index = _rows.IndexOf(row);
            if (index >= 0)
                App.Playback.PlayQueue(_tracks, index);
        }
    }
}

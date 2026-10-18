using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Detail satu album, bergaya Apple Music. Parameter navigasinya adalah AlbumSummary.</summary>
public sealed partial class AlbumDetailPage : Page
{
    private const int CoverPixelWidth = 520; // dua kali ukuran tampilnya, supaya tetap tajam di layar HiDPI

    private List<Track> _tracks = [];
    private List<TrackRow> _rows = [];

    public AlbumDetailPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not AlbumSummary album)
            return;

        _tracks = App.Database.GetAlbumTracks(album.Artist, album.Title).ToList();
        _rows = _tracks.Select((track, index) => new TrackRow(track, index)).ToList();
        TrackList.ItemsSource = _rows;

        AlbumTitleText.Text = album.Title;
        ArtistText.Text = album.DisplayArtist;

        var meta = new List<string>();
        if (album.Genre is { Length: > 0 } genre)
            meta.Add(genre);
        if (album.Year is int year)
            meta.Add(year.ToString());
        MetaText.Text = string.Join(" · ", meta);

        if (_tracks.Count > 0)
        {
            var quality = AudioQuality.Describe(_tracks[0]);
            QualityBadgeText.Text = quality.Badge;
            ToolTipService.SetToolTip(QualityBadge, quality.Detail);
        }
        else
        {
            QualityBadge.Visibility = Visibility.Collapsed;
        }

        FooterYearText.Text = album.Year?.ToString() ?? "";
        FooterCountText.Text = $"{album.TrackCount} songs, {Math.Round(album.Duration.TotalMinutes)} minutes";

        // Tandai lagu yang sedang diputar, dan ikuti terus saat lagunya berganti
        App.Playback.PropertyChanged += Playback_PropertyChanged;
        MarkNowPlaying();

        CoverBrush.ImageSource = await CoverCache.GetAsync(album.CoverTrackPath, CoverPixelWidth);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
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

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0)
            App.Playback.PlayQueue(_tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0)
            App.Playback.PlayQueue(_tracks.OrderBy(_ => Random.Shared.Next()).ToList(), 0);
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan isi album ini sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
        {
            int index = _rows.IndexOf(row);
            if (index >= 0)
                App.Playback.PlayQueue(_tracks, index);
        }
    }
}

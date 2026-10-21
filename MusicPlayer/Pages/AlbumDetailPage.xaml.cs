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

/// <summary>Detail satu album, bergaya Apple Music. Parameter navigasinya adalah AlbumSummary.</summary>
public sealed partial class AlbumDetailPage : Page
{
    private const int CoverPixelWidth = 520; // dua kali ukuran tampilnya, supaya tetap tajam di layar HiDPI

    private List<Track> _tracks = [];
    private List<TrackRow> _rows = [];
    private string _artist = "";

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
        _rows = _tracks.Select((track, index) => new TrackRow(track, index, album.DisplayArtist)).ToList();
        TrackList.ItemsSource = _rows;

        AlbumTitleText.Text = album.Title;
        ArtistText.Text = album.DisplayArtist;

        // Sama seperti "Go to Artist" di menu klik kanan: artis utama, tanpa bagian "feat."
        var firstTrack = _tracks.FirstOrDefault();
        _artist = firstTrack is null ? "" : ArtistNames.Primary(firstTrack.AlbumArtist ?? firstTrack.Artist);
        ArtistLink.IsEnabled = _artist.Length > 0;

        var meta = new List<string>();
        if (album.Genre is { Length: > 0 } genre)
            meta.Add(genre);
        if (album.Year is int year)
            meta.Add(year.ToString());
        MetaText.Text = string.Join(" · ", meta);

        if (_tracks.Count > 0)
        {
            var quality = AudioQuality.Describe(_tracks[0]);
            if (meta.Count > 0)
                MetaText.Text += " ·";
            QualityBadgeText.Text = quality.ShortBadge; // "Hi-Res", "Lossless", atau codec lossy seperti "MP3"
            HiResIcon.Visibility = quality.Kind == QualityKind.HiRes ? Visibility.Visible : Visibility.Collapsed;
            LosslessIcon.Visibility = quality.Kind == QualityKind.Lossless ? Visibility.Visible : Visibility.Collapsed;
            LossyIcon.Visibility = quality.Kind == QualityKind.Lossy ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(QualityBadge, quality.Detail);
        }
        else
        {
            QualityBadge.Visibility = Visibility.Collapsed;
        }

        // Tanggal rilis dan copyright diambil dari lagu pertama yang punya tag itu
        string? releaseDate = _tracks.Select(t => t.ReleaseDate).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
        FooterYearText.Text = Formatters.ReleaseDate(releaseDate, album.Year);
        int minutes = (int)Math.Round(album.Duration.TotalMinutes);
        FooterCountText.Text = $"{Formatters.Count(album.TrackCount, "song")}, {Formatters.Count(minutes, "minute")}, "
            + Formatters.FileSize(_tracks.Sum(t => t.FileSize));
        string? copyright = _tracks.Select(t => t.Copyright).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        FooterCopyrightText.Text = copyright ?? "";
        FooterCopyrightText.Visibility = copyright is null ? Visibility.Collapsed : Visibility.Visible;

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

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (_artist.Length > 0)
            App.MainWindow.ShowPage(typeof(ArtistsPage), _artist);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0)
            App.Playback.PlayQueue(_tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        App.Playback.PlayShuffled(_tracks);
    }

    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
            TrackMenu.Show(TrackList, e.GetPosition(TrackList), _tracks, _rows.IndexOf(row), goToAlbum: false);
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

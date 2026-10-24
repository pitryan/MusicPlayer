using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Badge kualitas beserta popup-nya, sama di jendela utama, MiniPlayer, dan layar penuh.</summary>
public sealed partial class QualityBadge : UserControl
{
    public QualityBadge()
    {
        InitializeComponent();
        QualityFlyout.Opening += (_, _) => ShowSourceState();
        // Hanya mendengarkan selama tampil: badge di MiniPlayer dan layar penuh ikut dibuang bersama jendelanya
        Loaded += (_, _) => Playback.PropertyChanged += Playback_PropertyChanged;
        Unloaded += (_, _) => Playback.PropertyChanged -= Playback_PropertyChanged;
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void AudioSettings_Click(object sender, RoutedEventArgs e)
    {
        QualityFlyout.Hide();
        App.MainWindow.OpenAudioSettings();
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
            ShowSourceState();
    }

    /// <summary>Hasil yang sudah pernah diperiksa untuk lagu ini, atau tombol untuk memeriksanya.</summary>
    private void ShowSourceState()
    {
        SourceProgress.IsActive = false;
        SourceProgress.Visibility = Visibility.Collapsed;
        var track = Playback.CurrentTrack;
        if (track is not null && App.QualityChecks.Cached(track) is { } report)
        {
            ShowReport(track, report);
            return;
        }

        SourcePanel.Visibility = Visibility.Collapsed;
        CheckSourceButton.Visibility = track is null ? Visibility.Collapsed : Visibility.Visible;
        CheckSourceButton.IsEnabled = true;
    }

    private async void CheckSource_Click(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentTrack is not { } track)
            return;

        CheckSourceButton.IsEnabled = false;
        SourceProgress.IsActive = true;
        SourceProgress.Visibility = Visibility.Visible;
        try
        {
            var report = await App.QualityChecks.AnalyzeAsync(track);
            if (Playback.CurrentTrack?.FilePath == track.FilePath)
                ShowReport(track, report);
        }
        catch (Exception ex)
        {
            SourceVerdictText.Text = "Couldn't analyze this song";
            SourceDetailText.Text = ex.Message;
            SourcePanel.Visibility = Visibility.Visible;
            CheckSourceButton.IsEnabled = true;
        }
        finally
        {
            SourceProgress.IsActive = false;
            SourceProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowReport(Track track, QualityReport report)
    {
        CheckSourceButton.Visibility = Visibility.Collapsed;
        SourcePanel.Visibility = Visibility.Visible;
        SourceVerdictText.Text = Formatters.SourceVerdictText(report, track);
        SourceDetailText.Text = Formatters.SourceDetailText(report, track);
        Spectrum.Show(report);
    }
}

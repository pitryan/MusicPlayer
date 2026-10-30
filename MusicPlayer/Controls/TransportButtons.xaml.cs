using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Helpers;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Tombol Previous, Play/Pause, dan Next. Satu kontrol untuk semua jendela, supaya tampilannya selalu sama.</summary>
public sealed partial class TransportButtons : UserControl
{
    public TransportButtons()
    {
        InitializeComponent();
        // Hanya mendengarkan selama tampil: kontrol di MiniPlayer dan layar penuh ikut dibuang bersama jendelanya
        Loaded += (_, _) =>
        {
            Playback.PropertyChanged += Playback_PropertyChanged;
            // MiniPlayer dan layar penuh bisa dibuka saat lagu sedang diputar
            UpdatePlayPauseName();
        };
        Unloaded += (_, _) => Playback.PropertyChanged -= Playback_PropertyChanged;
    }

    public PlaybackService Playback { get; } = App.Playback;

    // Ikon Play/Pause berganti dengan denyut singkat, dari mana pun pemutarannya diubah (tombol, keyboard, tombol media)
    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.IsPlaying))
        {
            Animations.Pop(PlayPauseButton);
            UpdatePlayPauseName();
        }
    }

    // Nama untuk Narrator dan UI Automation menyebut apa yang dilakukan tombol sekarang, bukan "Play / Pause" seperti tooltip
    private void UpdatePlayPauseName() =>
        AutomationProperties.SetName(PlayPauseButton, Playback.IsPlaying ? "Pause" : "Play");

    private void Previous_Click(object sender, RoutedEventArgs e) => Playback.Previous();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Playback.TogglePlayPause();

    private void Next_Click(object sender, RoutedEventArgs e) => Playback.Next();
}

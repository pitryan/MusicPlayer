using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Tombol Previous, Play/Pause, dan Next. Satu kontrol untuk semua jendela, supaya tampilannya selalu sama.</summary>
public sealed partial class TransportButtons : UserControl
{
    public TransportButtons()
    {
        InitializeComponent();
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void Previous_Click(object sender, RoutedEventArgs e) => Playback.Previous();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Playback.TogglePlayPause();

    private void Next_Click(object sender, RoutedEventArgs e) => Playback.Next();
}

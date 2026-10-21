using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Badge kualitas beserta popup-nya, sama di jendela utama, MiniPlayer, dan layar penuh.</summary>
public sealed partial class QualityBadge : UserControl
{
    public QualityBadge()
    {
        InitializeComponent();
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void AudioSettings_Click(object sender, RoutedEventArgs e)
    {
        QualityFlyout.Hide();
        App.MainWindow.OpenAudioSettings();
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Shuffle, Repeat, dan Autoplay untuk panel antrean (lihat XAML-nya).</summary>
public sealed partial class QueueModeButtons : UserControl
{
    public QueueModeButtons()
    {
        InitializeComponent();
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void Shuffle_Click(object sender, RoutedEventArgs e) => Playback.IsShuffle = ShuffleToggle.IsChecked == true;

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        Playback.CycleRepeat();
        // Tiga keadaan (Off, All, One): dari All ke One tombol tetap menyala
        RepeatToggle.IsChecked = Playback.IsRepeatOn;
    }

    private void Autoplay_Click(object sender, RoutedEventArgs e) => Playback.AutoplayEnabled = AutoplayToggle.IsChecked == true;
}

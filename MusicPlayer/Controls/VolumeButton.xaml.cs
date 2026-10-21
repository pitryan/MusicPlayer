using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Tombol volume beserta popup-nya, sama di jendela utama dan MiniPlayer.</summary>
public sealed partial class VolumeButton : UserControl
{
    public VolumeButton()
    {
        InitializeComponent();
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void MuteToggle_Click(object sender, RoutedEventArgs e) =>
        Playback.IsMuted = MuteToggle.IsChecked == true;

    private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Scroll di atas tombol volume: naik/turun 2 per putaran roda
        int delta = e.GetCurrentPoint(VolumeIconButton).Properties.MouseWheelDelta;
        Playback.Volume += Math.Sign(delta) * 2;
        e.Handled = true;
    }
}

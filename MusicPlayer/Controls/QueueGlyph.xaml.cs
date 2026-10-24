using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>Ikon tombol antrean dengan lencana Autoplay (lihat XAML-nya).</summary>
public sealed partial class QueueGlyph : UserControl
{
    public QueueGlyph()
    {
        InitializeComponent();
    }

    public PlaybackService Playback { get; } = App.Playback;
}

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu baris lagu di halaman album. Saat lagu ini sedang diputar, nomor track-nya diganti
/// ikon play merah, seperti di Apple Music.
/// </summary>
public sealed class TrackRow : ObservableObject
{
    // Baris ke-1, 3, 5, ... sedikit lebih terang, supaya baris mudah diikuti mata
    private static readonly Brush StripeBrush = new SolidColorBrush(ColorHelper.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ClearBrush = new SolidColorBrush(Colors.Transparent);

    private bool _isPlaying;

    public TrackRow(Track track, int index)
    {
        Track = track;
        RowBackground = index % 2 == 0 ? StripeBrush : ClearBrush;
        NumberText = Formatters.TrackNumber(track.TrackNumber);
        ArtistText = track.DisplayArtist;
        DurationText = Formatters.Time(track.Duration);
    }

    public Track Track { get; }

    public string Title => Track.DisplayTitle;

    public string NumberText { get; }

    /// <summary>
    /// Artis lagu ini. Selalu ditampilkan, supaya album kompilasi (Various Artists)
    /// tetap menunjukkan siapa penyanyi setiap lagunya.
    /// </summary>
    public string ArtistText { get; }

    public string DurationText { get; }

    /// <summary>
    /// Latar belang dipasang di dalam template baris, bukan di kotak ListViewItem-nya.
    /// Latar kotak ListViewItem ikut diubah oleh efek hover, sehingga belangnya bisa hilang.
    /// </summary>
    public Brush RowBackground { get; }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(NumberVisibility));
                OnPropertyChanged(nameof(PlayingVisibility));
            }
        }
    }

    public Visibility NumberVisibility => IsPlaying ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PlayingVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;
}

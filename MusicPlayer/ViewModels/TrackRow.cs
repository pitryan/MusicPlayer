using Microsoft.UI.Xaml;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu baris lagu di halaman album. Saat lagu ini sedang diputar, nomor track-nya diganti
/// ikon play merah, seperti di Apple Music.
/// </summary>
public sealed class TrackRow : ObservableObject
{
    private bool _isPlaying;

    public TrackRow(Track track)
    {
        Track = track;
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

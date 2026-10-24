using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu lagu di antrean Playing Next. Sengaja berupa objek tersendiri (bukan Track langsung),
/// supaya lagu yang sama bisa masuk antrean dua kali dan tetap bisa digeser atau dihapus satu per satu.
/// </summary>
public sealed class QueueEntry : ObservableObject
{
    private const int CoverPixelWidth = 96;

    private ImageSource? _cover;
    private bool _coverRequested;

    public QueueEntry(Track track)
    {
        Track = track;
    }

    public Track Track { get; private set; }

    /// <summary>Lagu tambahan dari Autoplay (bukan antrean sendiri); tampil sedikit redup, seperti Apple Music.</summary>
    public bool IsAutoplay { get; init; }

    public double ItemOpacity => IsAutoplay ? 0.6 : 1;

    /// <summary>Data lagu baru setelah tag-nya diedit; judul dan artis di panel antrean ikut berubah.</summary>
    public void UpdateTrack(Track track)
    {
        Track = track;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(DurationText));
    }

    public string Title => Track.DisplayTitle;

    public string Subtitle => $"{Track.DisplayArtist} — {Track.DisplayAlbum}";

    public string DurationText => Formatters.Time(Track.Duration);

    public ImageSource? Cover
    {
        get => _cover;
        private set => SetProperty(ref _cover, value);
    }

    public async void EnsureCover()
    {
        if (_coverRequested)
            return;

        _coverRequested = true;
        Cover = await CoverCache.GetAsync(Track.FilePath, CoverPixelWidth);
    }
}

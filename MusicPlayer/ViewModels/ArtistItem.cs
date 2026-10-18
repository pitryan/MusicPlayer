using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu artis di daftar. Fotonya memakai cover album terbaru si artis, dimuat saat akan tampil.</summary>
public sealed class ArtistItem : ObservableObject
{
    private const int CoverPixelWidth = 240;

    private ImageSource? _cover;
    private bool _coverRequested;

    public ArtistItem(ArtistSummary summary)
    {
        Summary = summary;
    }

    public ArtistSummary Summary { get; }

    public string Name => Summary.DisplayName;

    /// <summary>Contoh: "2 albums, 15 songs".</summary>
    public string CountText =>
        $"{Formatters.Count(Summary.AlbumCount, "album")}, {Formatters.Count(Summary.TrackCount, "song")}";

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
        Cover = await CoverCache.GetAsync(Summary.CoverTrackPath, CoverPixelWidth);
    }
}

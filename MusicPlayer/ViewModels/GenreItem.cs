using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu kotak genre. Gambarnya memakai cover salah satu lagu bergenre itu.</summary>
public sealed class GenreItem : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public GenreItem(GenreSummary summary)
    {
        Summary = summary;
    }

    public GenreSummary Summary { get; }

    public string Name => Summary.Name;

    /// <summary>Contoh: "12 albums, 85 songs".</summary>
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
        Cover = await CoverCache.GetAsync(Summary.CoverTrackPath, AlbumItem.CoverPixelWidth);
    }
}

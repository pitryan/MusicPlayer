using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu subfolder di halaman folder. Gambarnya memakai cover lagu pertama di dalam folder itu.</summary>
public sealed class FolderItem : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public FolderItem(FolderSummary summary)
    {
        Summary = summary;
    }

    public FolderSummary Summary { get; }

    public string Name => Summary.Name;

    public string CountText => Formatters.Count(Summary.TrackCount, "song");

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

/// <summary>Satu bagian di jalur folder (breadcrumb), misalnya "music" › "Japan" › "YOASOBI".</summary>
public sealed record FolderCrumb(string Name, string Path)
{
    // BreadcrumbBar menampilkan teks dari ToString()
    public override string ToString() => Name;
}

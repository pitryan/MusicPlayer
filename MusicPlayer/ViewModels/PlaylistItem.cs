using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu kotak di All Playlists. Cover-nya seperti Apple Music: mozaik 2×2 kalau isinya dari 4 album atau lebih,
/// cover album pertama kalau kurang, dan bintang merah untuk Favourite Songs. Cover baru dimuat saat kotaknya tampil.
/// </summary>
public sealed class PlaylistItem : ObservableObject
{
    private const int CoverPixelWidth = 360; // dua kali ukuran tampilnya (180 px)

    private ImageSource? _cover1, _cover2, _cover3, _cover4;
    private bool _coversRequested;

    public PlaylistItem(PlaylistSummary summary)
    {
        Summary = summary;
        Name = summary.Name;
        CountText = Formatters.Count(summary.TrackCount, "song");
    }

    private PlaylistItem(int favouriteCount)
    {
        Name = "Favourite Songs";
        CountText = Formatters.Count(favouriteCount, "song");
    }

    public static PlaylistItem Favourites(int count) => new(count);

    /// <summary>Null untuk Favourite Songs.</summary>
    public PlaylistSummary? Summary { get; }

    public string Name { get; }

    public string CountText { get; }

    private int CoverCount => Summary?.CoverTrackPaths.Count ?? 0;

    public Visibility StarVisibility => Summary is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MosaicVisibility => CoverCount >= 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SingleVisibility => CoverCount is >= 1 and < 4 ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Cover1 { get => _cover1; private set => SetProperty(ref _cover1, value); }
    public ImageSource? Cover2 { get => _cover2; private set => SetProperty(ref _cover2, value); }
    public ImageSource? Cover3 { get => _cover3; private set => SetProperty(ref _cover3, value); }
    public ImageSource? Cover4 { get => _cover4; private set => SetProperty(ref _cover4, value); }

    public async void EnsureCovers()
    {
        if (_coversRequested || Summary is null)
            return;

        _coversRequested = true;
        var paths = Summary.CoverTrackPaths;
        // Mozaik memakai separuh ukuran untuk setiap kotak kecilnya
        int pixels = paths.Count >= 4 ? CoverPixelWidth / 2 : CoverPixelWidth;
        if (paths.Count >= 1) Cover1 = await CoverCache.GetAsync(paths[0], pixels);
        if (paths.Count >= 4)
        {
            Cover2 = await CoverCache.GetAsync(paths[1], pixels);
            Cover3 = await CoverCache.GetAsync(paths[2], pixels);
            Cover4 = await CoverCache.GetAsync(paths[3], pixels);
        }
    }
}

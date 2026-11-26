using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu baris peringkat di halaman Wrapped (lagu, artis, atau album). <see cref="Target"/> adalah datanya dari
/// Wrapped.Build: RankedTrack, RankedArtist, atau RankedAlbum.
/// </summary>
public sealed class WrappedItem : ObservableObject
{
    private readonly string _coverPath;
    private readonly int _coverWidth;
    private ImageSource? _cover;
    private bool _coverRequested;

    public WrappedItem(int rank, string title, string subtitle, string detail, string coverPath, object target, int coverWidth = 96)
    {
        Rank = rank;
        Title = title;
        Subtitle = subtitle;
        Detail = detail;
        Target = target;
        _coverPath = coverPath;
        _coverWidth = coverWidth;
    }

    public int Rank { get; }

    public string RankText => Rank.ToString();

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>Contoh: "12 plays".</summary>
    public string Detail { get; }

    public object Target { get; }

    public ImageSource? Cover
    {
        get => _cover;
        private set => SetProperty(ref _cover, value);
    }

    /// <summary>Cover dimuat saat barisnya tampil, sekali saja.</summary>
    public async void EnsureCover()
    {
        if (_coverRequested || _coverPath.Length == 0)
            return;
        _coverRequested = true;
        Cover = await CoverCache.GetAsync(_coverPath, _coverWidth);
    }
}

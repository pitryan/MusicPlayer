using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu kotak album di grid. Cover-nya baru dimuat saat kotaknya akan tampil di layar.</summary>
public sealed class AlbumItem : ObservableObject
{
    public const int CoverPixelWidth = 360; // dua kali ukuran tampilnya, supaya tetap tajam di layar HiDPI

    private ImageSource? _cover;
    private bool _coverRequested;

    public AlbumItem(AlbumSummary summary)
    {
        Summary = summary;
    }

    public AlbumSummary Summary { get; }

    public string Title => Summary.Title;

    public string Artist => Summary.DisplayArtist;

    public string YearText => Summary.Year?.ToString() ?? "";

    /// <summary>
    /// Baris kedua di bawah judul. Biasanya nama artis; di halaman artis diganti tahun (dan jenis rilis), karena
    /// artisnya sudah jelas.
    /// </summary>
    public string Subtitle
    {
        get => _subtitle ?? Artist;
        init => _subtitle = value;
    }

    private readonly string? _subtitle;

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

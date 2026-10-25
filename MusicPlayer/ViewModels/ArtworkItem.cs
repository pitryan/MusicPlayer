using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu pilihan cover dari Cover Art Archive di halaman Change Artwork.</summary>
public sealed class ArtworkItem : ObservableObject
{
    // Thumbnail 250 px dari Cover Art Archive, ditampilkan 160 px (cukup tajam di layar 150%)
    private const int ThumbnailSize = 250;
    private ImageSource? _thumbnail;

    public ArtworkItem(ArtworkCandidate candidate)
    {
        Candidate = candidate;
    }

    public ArtworkCandidate Candidate { get; }

    public string Title => Candidate.Title;

    public string Detail => Candidate.Detail;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>Memuat thumbnail-nya. False kalau rilis ini ternyata tidak punya cover di Cover Art Archive.</summary>
    public async Task<bool> LoadThumbnailAsync(CancellationToken ct)
    {
        byte[]? bytes = await App.MusicBrainz.GetFrontCoverAsync(Candidate.ReleaseId, ThumbnailSize, ct);
        if (bytes is null)
            return false;
        Thumbnail = await CoverImageLoader.FromBytesAsync(bytes, ThumbnailSize);
        return Thumbnail is not null;
    }
}

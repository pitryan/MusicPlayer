using System.Linq;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu rilis di hasil pencarian MusicBrainz, dengan ringkasan untuk memilih edisi yang tepat.</summary>
public sealed class MbResultItem : ObservableObject
{
    private const int ThumbnailSize = 250; // ukuran terkecil di Cover Art Archive

    private ImageSource? _cover;
    private bool _coverRequested;

    public MbResultItem(MbRelease release)
    {
        Release = release;
        ArtistText = MusicBrainzTagger.CreditText(release.Credits, preferEnglishNames: false, sort: false);

        // Contoh: "2024-03-05 · JP · 2× Digital Media · 4 tracks · Single · Colorful Palette CPCD-0001"
        var formats = release.Media.GroupBy(m => m.Format ?? "Unknown format")
            .Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key);
        var type = string.Join(" + ", new[] { release.PrimaryType }.Concat(release.SecondaryTypes).Where(t => !string.IsNullOrEmpty(t)));
        var label = release.Labels.FirstOrDefault();
        var parts = new[]
        {
            release.Date, release.Country, string.Join(", ", formats), Formatters.Count(release.TrackCount, "track"), type,
            label is null ? null : $"{label.Name} {label.CatalogNumber}".Trim(),
            release.Status is { } status && status != "Official" ? status : null,
        };
        DetailsText = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public MbRelease Release { get; }

    public string Title => Release.Title;

    public string ArtistText { get; }

    public string DetailsText { get; }

    public ImageSource? Cover
    {
        get => _cover;
        private set => SetProperty(ref _cover, value);
    }

    /// <summary>Cover kecil dari Cover Art Archive, baru diambil saat barisnya tampil.</summary>
    public async void EnsureCover()
    {
        if (_coverRequested)
            return;
        _coverRequested = true;

        try
        {
            if (await App.MusicBrainz.GetFrontCoverAsync(Release.Id, ThumbnailSize, default) is { } data)
                Cover = await CoverImageLoader.FromBytesAsync(data, 96);
        }
        catch (System.Exception)
        {
            // Tanpa cover pun hasil pencarian tetap bisa dipilih
        }
    }
}

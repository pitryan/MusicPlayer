using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu lagu di halaman Quality Check: format di file, dan kesimpulan dari isinya.</summary>
public sealed class QualityRow : ObservableObject
{
    private const int CoverPixelWidth = 80;

    private ImageSource? _cover;
    private bool _coverRequested;

    public QualityRow(Track track, QualityReport report)
    {
        Track = track;
        Report = report;
        IsSuspicious = Formatters.IsSuspicious(report, track);
        VerdictText = Formatters.SourceVerdictText(report, track);
        // Bit tambalan disebut di kesimpulan, karena tidak terlihat di grafik
        if (track.BitsPerSample >= 24 && !report.IsFloat && report.EffectiveBits is <= 16)
            VerdictText += " · only 16 bits used";
    }

    public Track Track { get; }

    /// <summary>Ringkasan tanpa spektrum (spektrum diambil saat barisnya dipilih).</summary>
    public QualityReport Report { get; }

    public bool IsSuspicious { get; }

    public string Title => Track.DisplayTitle;

    public string Subtitle => $"{Track.DisplayArtist} — {Track.DisplayAlbum}";

    /// <summary>Format yang tertulis di file, misalnya "24-bit 96 kHz FLAC".</summary>
    public string FormatText => AudioQuality.Describe(Track).Detail;

    public string VerdictText { get; }

    public Visibility SuspiciousVisibility => IsSuspicious ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NormalVisibility => IsSuspicious ? Visibility.Collapsed : Visibility.Visible;

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

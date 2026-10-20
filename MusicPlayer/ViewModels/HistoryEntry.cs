using System;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu baris di tab History: lagu yang pernah diputar dan kapan.</summary>
public sealed class HistoryEntry : ObservableObject
{
    private const int CoverPixelWidth = 96;

    private ImageSource? _cover;
    private bool _coverRequested;

    public HistoryEntry(PlayRecord record)
    {
        Track = record.Track;
        PlayedText = Formatters.PlayedAt(record.PlayedUtc);
    }

    public Track Track { get; }

    public string Title => Track.DisplayTitle;

    public string Subtitle => $"{Track.DisplayArtist} — {Track.DisplayAlbum}";

    /// <summary>Contoh: "14:05", "Yesterday 21:30", atau "12 Oct".</summary>
    public string PlayedText { get; }

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

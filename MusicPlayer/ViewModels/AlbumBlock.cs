using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

/// <summary>Satu album di halaman artis: cover di kiri, daftar lagunya di kanan.</summary>
public sealed class AlbumBlock : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public AlbumBlock(AlbumSummary summary, IReadOnlyList<Track> tracks)
    {
        Summary = summary;
        Tracks = tracks;
        Rows = tracks.Select((track, index) => new TrackRow(track, index)).ToList();
    }

    public AlbumSummary Summary { get; }

    public IReadOnlyList<Track> Tracks { get; }

    public List<TrackRow> Rows { get; }

    public string Title => Summary.Title;

    /// <summary>Contoh: "J-Pop · 2024 · 10 songs".</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Summary.Genre is { Length: > 0 } genre)
                parts.Add(genre);
            if (Summary.Year is int year)
                parts.Add(year.ToString());
            parts.Add(Formatters.Count(Summary.TrackCount, "song"));
            return string.Join(" · ", parts);
        }
    }

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

using Microsoft.UI.Xaml;

namespace MusicPlayer.ViewModels;

/// <summary>Satu file di pratinjau MusicBrainz: lagu rilis yang dipasangkan dengannya, atau tidak ada.</summary>
public sealed class MbMatchRow(string fileName, string matchText, bool isMatched)
{
    public string FileName { get; } = fileName;

    /// <summary>Contoh: "1.2  Passion at 25:00 (4:04)", atau "No match – this song won't be changed".</summary>
    public string MatchText { get; } = matchText;

    public Visibility MatchedVisibility { get; } = isMatched ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnmatchedVisibility { get; } = isMatched ? Visibility.Collapsed : Visibility.Visible;
}

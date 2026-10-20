namespace MusicPlayer.Library;

/// <summary>Satu subfolder di halaman folder. TrackCount menghitung semua lagu di dalamnya, termasuk subfolder-nya.</summary>
public sealed record FolderSummary(string Path, string Name, int TrackCount, string CoverTrackPath);

/// <summary>Satu genre di halaman Genres.</summary>
public sealed record GenreSummary(string Name, int AlbumCount, int TrackCount, string CoverTrackPath);

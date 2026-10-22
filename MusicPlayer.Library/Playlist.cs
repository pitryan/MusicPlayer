namespace MusicPlayer.Library;

/// <summary>
/// Ringkasan satu playlist untuk sidebar dan grid All Playlists. <see cref="CoverTrackPaths"/> berisi paling banyak
/// 4 lagu dari album yang berbeda, untuk cover mozaik.
/// </summary>
public sealed record PlaylistSummary(
    long Id, string Name, int TrackCount, TimeSpan Duration, IReadOnlyList<string> CoverTrackPaths, DateTime ModifiedUtc);

/// <summary>Satu isi playlist. <see cref="ItemId"/> membedakan lagu yang sama yang dimasukkan lebih dari sekali.</summary>
public sealed record PlaylistEntry(long ItemId, Track Track);

using System;
using System.Collections.Generic;
using System.Linq;
using MusicPlayer.Library;

namespace MusicPlayer.ViewModels;

/// <summary>Satu kelompok di halaman Recently Added, misalnya "This Week" beserta album-albumnya.</summary>
public sealed class AlbumGroup : List<AlbumItem>
{
    public AlbumGroup(string title, IEnumerable<AlbumItem> albums)
        : base(albums)
    {
        Title = title;
    }

    public string Title { get; }

    /// <summary>Album terbaru lebih dulu, dikelompokkan seperti di Apple Music.</summary>
    public static List<AlbumGroup> ByDateAdded(IEnumerable<AlbumItem> albums, DateTime nowUtc) =>
        albums
            .OrderByDescending(a => a.Summary.AddedUtc)
            .GroupBy(a => RecentlyAdded.GroupTitle(a.Summary.AddedUtc, nowUtc))
            .Select(g => new AlbumGroup(g.Key, g))
            .ToList();
}

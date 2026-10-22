using System;
using System.Collections.Generic;
using System.Linq;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Playlist buatan sendiri, dipakai bersama lewat <see cref="App.Playlists"/>. Semua perubahan lewat sini, supaya
/// sidebar, All Playlists, dan halaman playlist yang terbuka ikut diperbarui lewat <see cref="Changed"/>.
/// </summary>
public sealed class PlaylistsService
{
    private readonly LibraryDatabase _database;

    public PlaylistsService(LibraryDatabase database) => _database = database;

    /// <summary>Dipicu setiap kali playlist dibuat, diganti nama, dihapus, atau isinya berubah.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PlaylistSummary> GetAll() => _database.GetPlaylists();

    public PlaylistSummary? Find(long id) => GetAll().FirstOrDefault(p => p.Id == id);

    public IReadOnlyList<PlaylistEntry> GetEntries(long id) => _database.GetPlaylistEntries(id);

    public long Create(string name)
    {
        long id = _database.CreatePlaylist(name.Trim(), DateTime.UtcNow);
        Raise();
        return id;
    }

    public void Rename(long id, string name)
    {
        _database.RenamePlaylist(id, name.Trim(), DateTime.UtcNow);
        Raise();
    }

    public void Delete(long id)
    {
        _database.DeletePlaylist(id);
        Raise();
    }

    /// <summary>Berapa lagu dari <paramref name="tracks"/> yang sudah ada di playlist itu.</summary>
    public int CountExisting(long id, IReadOnlyList<Track> tracks)
    {
        var existing = new HashSet<string>(_database.GetPlaylistPaths(id), StringComparer.OrdinalIgnoreCase);
        return tracks.Count(t => existing.Contains(t.FilePath));
    }

    /// <summary>
    /// Menambahkan lagu ke akhir playlist. Dengan <paramref name="skipExisting"/>, lagu yang sudah ada di playlist
    /// (atau muncul dua kali di daftar ini) tidak ditambahkan lagi. Hasilnya: jumlah lagu yang benar-benar ditambahkan.
    /// </summary>
    public int Add(long id, IReadOnlyList<Track> tracks, bool skipExisting)
    {
        var paths = tracks.Select(t => t.FilePath).ToList();
        if (skipExisting)
        {
            var seen = new HashSet<string>(_database.GetPlaylistPaths(id), StringComparer.OrdinalIgnoreCase);
            paths = paths.Where(seen.Add).ToList();
        }

        _database.AddToPlaylist(id, paths, DateTime.UtcNow);
        if (paths.Count > 0)
            Raise();
        return paths.Count;
    }

    public void Remove(long id, IReadOnlyCollection<long> itemIds)
    {
        _database.RemoveFromPlaylist(id, itemIds, DateTime.UtcNow);
        Raise();
    }

    public void Reorder(long id, IReadOnlyList<long> itemIdsInOrder)
    {
        _database.ReorderPlaylist(id, itemIdsInOrder, DateTime.UtcNow);
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

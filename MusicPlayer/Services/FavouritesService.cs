using System;
using System.Collections.Generic;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Lagu favorit (bintang ☆), dipakai bersama lewat <see cref="App.Favourites"/>. Disimpan di database lewat path
/// file, dan disalin ke memori supaya setiap baris lagu bisa mengecek statusnya tanpa query.
/// </summary>
public sealed class FavouritesService
{
    private readonly LibraryDatabase _database;
    private readonly HashSet<string> _paths;

    public FavouritesService(LibraryDatabase database)
    {
        _database = database;
        _paths = new HashSet<string>(database.GetFavouritePaths(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dipicu setiap kali sebuah lagu difavoritkan atau dilepas, dari mana pun (LCD, menu, halaman lain), supaya
    /// bintang di semua tempat ikut berubah.
    /// </summary>
    public event EventHandler? Changed;

    public bool IsFavourite(Track? track) => track is not null && _paths.Contains(track.FilePath);

    public void Toggle(Track track) => Set(track, !IsFavourite(track));

    public void Set(Track track, bool favourite)
    {
        if (favourite == IsFavourite(track))
            return;

        _database.SetFavourite(track.FilePath, favourite, DateTime.UtcNow);
        if (favourite)
            _paths.Add(track.FilePath);
        else
            _paths.Remove(track.FilePath);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

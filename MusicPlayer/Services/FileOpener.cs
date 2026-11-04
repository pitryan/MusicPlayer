using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Lagu yang dibuka dari luar aplikasi: "Open with" atau klik dua kali di Explorer (lihat App), dan file atau folder yang
/// di-drop ke jendela. Lagu yang ada di library memakai datanya dari sana; yang tidak ada dibaca langsung dari tag-nya,
/// tanpa ditambahkan ke library. Hanya dipakai dari UI thread.
/// </summary>
public static class FileOpener
{
    /// <summary>
    /// Memutar file dan folder ini sebagai antrean baru, atau menambahkannya ke bawah Playing Next. File yang bukan audio
    /// dilewati; kalau tidak ada lagu sama sekali, tidak ada yang berubah.
    /// </summary>
    public static async Task OpenAsync(IReadOnlyList<string> paths, bool addToQueue = false)
    {
        var database = App.Database;
        List<Track> tracks;
        try
        {
            tracks = await Task.Run(() => AudioFiles.ToTracks(database, AudioFiles.Expand(paths)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // misalnya folder yang di-drop tidak boleh dibaca: tidak ada yang berubah
        }
        if (tracks.Count == 0)
            return;

        if (!addToQueue)
        {
            App.Playback.PlayQueue(tracks, 0);
            return;
        }

        // Kalau belum ada yang diputar, lagu pertama langsung diputar dan sisanya masuk antrean
        foreach (var track in tracks)
            App.Playback.AddToQueue(track);
    }
}

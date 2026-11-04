using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Melanjutkan sesi terakhir: antrean, lagu sekarang, dan posisinya disimpan ke file, lalu dipulihkan saat aplikasi
/// dibuka lagi (di-pause, tanpa membuka DAC). Disimpan beberapa detik setelah lagu, antrean, atau status putar berubah,
/// setiap 30 detik selama lagu berbunyi (supaya posisinya tidak jauh tertinggal kalau aplikasi crash), dan saat aplikasi
/// ditutup. Hanya dipakai dari UI thread.
/// </summary>
public sealed class SessionService
{
    // Perubahan beruntun (misalnya antrean yang diisi ulang satu per satu) cukup disimpan sekali
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PlayingSaveInterval = TimeSpan.FromSeconds(30);

    private readonly PlaybackService _playback;
    private readonly string _path;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _playingTimer;

    public SessionService(PlaybackService playback, string path, DispatcherQueue dispatcher)
    {
        _playback = playback;
        _path = path;

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = SaveDelay;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();

        _playingTimer = dispatcher.CreateTimer();
        _playingTimer.Interval = PlayingSaveInterval;
        _playingTimer.Tick += (_, _) =>
        {
            if (_playback.IsPlaying)
                SaveNow();
        };
    }

    /// <summary>
    /// Memulihkan sesi yang tersimpan (kecuali <paramref name="restore"/> false, misalnya aplikasi dibuka dengan file), lalu
    /// mulai menyimpan setiap perubahan. Dipanggil sekali saat aplikasi dibuka.
    /// </summary>
    public void Start(LibraryDatabase database, bool restore = true)
    {
        if (restore && PlaybackSessionFile.Load(_path) is { } session)
        {
            var paths = session.Previous.Concat(session.UpNext.Select(e => e.Path)).Append(session.Current);
            var tracks = database.GetTracksByPath(paths);
            _playback.RestoreSession(session, path => tracks.GetValueOrDefault(path));
        }

        _playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlaybackService.CurrentTrack) or nameof(PlaybackService.IsPlaying))
                ScheduleSave();
        };
        _playback.UpNext.CollectionChanged += (_, _) => ScheduleSave();
        _playingTimer.Start();
    }

    /// <summary>Menyimpan sekarang juga, misalnya saat aplikasi ditutup.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        PlaybackSessionFile.Save(_path, _playback.CaptureSession());
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}

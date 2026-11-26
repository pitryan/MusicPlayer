using System;
using Microsoft.UI.Dispatching;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Sleep timer: pause setelah sekian menit, atau berhenti setelah lagu atau album ini selesai. Yang di batas lagu
/// diputuskan <see cref="PlaybackService"/> (lihat SleepTimerRules); yang berdasarkan waktu memicu <see cref="Expired"/>.
/// Setelah berhenti, timer-nya mati sendiri.
/// </summary>
public sealed class SleepTimer : ObservableObject
{
    private readonly DispatcherQueueTimer _tick;
    private DateTime _deadlineUtc;
    private SleepTimerMode _mode;

    public SleepTimer(DispatcherQueue dispatcher)
    {
        _tick = dispatcher.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => OnTick();
    }

    /// <summary>Waktu untuk mode Timed habis: lagu di-pause.</summary>
    public event EventHandler? Expired;

    public SleepTimerMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    public bool IsActive => Mode != SleepTimerMode.Off;

    /// <summary>Sisa waktu untuk mode Timed.</summary>
    public TimeSpan Remaining => Mode == SleepTimerMode.Timed
        ? TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((_deadlineUtc - DateTime.UtcNow).TotalSeconds)))
        : TimeSpan.Zero;

    /// <summary>Contoh: "Pausing in 23:41", "Stopping after this song". Kosong kalau mati.</summary>
    public string Description => Mode switch
    {
        SleepTimerMode.Timed => $"Pausing in {FormatRemaining(Remaining)}",
        SleepTimerMode.EndOfSong => "Stopping after this song",
        SleepTimerMode.EndOfAlbum => "Stopping after this album",
        _ => "",
    };

    public void StartTimed(TimeSpan duration)
    {
        _deadlineUtc = DateTime.UtcNow + duration;
        _tick.Start();
        Mode = SleepTimerMode.Timed;
        OnPropertyChanged(nameof(Description)); // juga kalau tadi sudah Timed dengan waktu lain
    }

    public void StopAfterSong() => SetBoundary(SleepTimerMode.EndOfSong);

    public void StopAfterAlbum() => SetBoundary(SleepTimerMode.EndOfAlbum);

    public void Cancel()
    {
        _tick.Stop();
        Mode = SleepTimerMode.Off;
    }

    private void SetBoundary(SleepTimerMode mode)
    {
        _tick.Stop();
        Mode = mode;
    }

    private void OnTick()
    {
        if (Remaining > TimeSpan.Zero)
        {
            OnPropertyChanged(nameof(Remaining));
            OnPropertyChanged(nameof(Description));
            return;
        }
        Cancel();
        Expired?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"23:41", atau "1:05:00" untuk satu jam lebih.</summary>
    public static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1 ? remaining.ToString(@"h\:mm\:ss") : remaining.ToString(@"m\:ss");
}

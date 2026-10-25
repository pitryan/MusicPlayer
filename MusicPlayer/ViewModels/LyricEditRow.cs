using System;
using System.Collections.Generic;
using MusicPlayer.Helpers;

namespace MusicPlayer.ViewModels;

/// <summary>Satu baris di editor lirik: waktu (kalau bertanda waktu), lirik asli, cara baca, dan terjemahan.</summary>
public sealed class LyricEditRow : ObservableObject
{
    private string _original;
    private string _reading;
    private string _translation;

    public LyricEditRow(TimeSpan time, bool synced, string original, string? reading, string? translation, IReadOnlyList<string> extra)
    {
        Time = time;
        Synced = synced;
        _original = original;
        _reading = reading ?? "";
        _translation = translation ?? "";
        Extra = extra;
    }

    public TimeSpan Time { get; }

    public bool Synced { get; }

    /// <summary>Baris dwibahasa dari file LRC (waktu yang sama): tidak diedit di sini, tapi ikut disimpan.</summary>
    public IReadOnlyList<string> Extra { get; }

    /// <summary>"1:23.45" untuk lirik bertanda waktu, kosong untuk lirik biasa.</summary>
    public string TimeText => Synced ? $"{(int)Time.TotalMinutes}:{Time.Seconds:00}.{Time.Milliseconds / 10:00}" : "";

    public string Original
    {
        get => _original;
        set => SetProperty(ref _original, value);
    }

    public string Reading
    {
        get => _reading;
        set => SetProperty(ref _reading, value);
    }

    public string Translation
    {
        get => _translation;
        set => SetProperty(ref _translation, value);
    }
}

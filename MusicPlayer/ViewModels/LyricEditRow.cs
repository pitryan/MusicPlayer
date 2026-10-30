using System;
using System.Collections.Generic;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.ViewModels;

/// <summary>Satu baris di editor lirik: waktu (kalau bertanda waktu), lirik asli, cara baca, dan terjemahan.</summary>
public sealed class LyricEditRow : ObservableObject
{
    private string _original;
    private string _reading;
    private string _translation;

    private readonly string _loadedOriginal;
    private readonly IReadOnlyList<LyricWord> _words;
    private readonly TimeSpan? _wordsEnd;

    public LyricEditRow(TimeSpan time, bool synced, string original, string? reading, string? translation, IReadOnlyList<string> extra,
        IReadOnlyList<LyricWord>? words = null, TimeSpan? wordsEnd = null)
    {
        Time = time;
        Synced = synced;
        _original = original;
        _loadedOriginal = original;
        _reading = reading ?? "";
        _translation = translation ?? "";
        Extra = extra;
        _words = words ?? [];
        _wordsEnd = wordsEnd;
    }

    public TimeSpan Time { get; }

    public bool Synced { get; }

    /// <summary>Baris dwibahasa dari file LRC (waktu yang sama): tidak diedit di sini, tapi ikut disimpan.</summary>
    public IReadOnlyList<string> Extra { get; }

    /// <summary>
    /// Baris untuk disimpan. Waktu per kata (Enhanced LRC) tidak diedit di sini, tapi ikut disimpan selama lirik
    /// aslinya tidak diubah; kalau diubah, waktunya tidak cocok lagi dan dibuang.
    /// </summary>
    public LyricLine ToLine()
    {
        string text = Original.Trim();
        bool unchanged = text == _loadedOriginal.Trim();
        return new LyricLine(Time, text)
        {
            Extra = Extra,
            Words = unchanged ? _words : [],
            WordsEnd = unchanged ? _wordsEnd : null,
        };
    }

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

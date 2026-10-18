using System;

namespace MusicPlayer.Helpers;

/// <summary>Fungsi format yang bisa dipanggil langsung dari x:Bind di XAML.</summary>
public static class Formatters
{
    /// <summary>Contoh: 3:07, atau 1:02:45 untuk durasi lebih dari satu jam.</summary>
    public static string Time(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}

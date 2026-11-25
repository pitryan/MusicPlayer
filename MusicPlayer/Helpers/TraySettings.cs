using System;

namespace MusicPlayer.Helpers;

/// <summary>
/// Apa yang terjadi saat jendela utama ditutup atau di-minimize. Ikon di notification area tampil selama salah satunya
/// aktif. Bawaannya: menutup jendela tetap memutar lagu dari notification area, sedangkan minimize tetap ke taskbar,
/// supaya tombol Previous/Play/Next di thumbnail taskbar masih bisa dipakai.
/// </summary>
public static class TraySettings
{
    private const string CloseKey = "CloseToTray";
    private const string MinimizeKey = "MinimizeToTray";

    public static event EventHandler? Changed;

    public static bool CloseToTray
    {
        get => AppSettings.Get(CloseKey, true);
        set => Set(CloseKey, value);
    }

    public static bool MinimizeToTray
    {
        get => AppSettings.Get(MinimizeKey, false);
        set => Set(MinimizeKey, value);
    }

    public static bool ShowIcon => CloseToTray || MinimizeToTray;

    private static void Set(string key, bool value)
    {
        if (AppSettings.Get(key, !value) == value)
            return;
        AppSettings.Set(key, value);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

using Windows.System;

namespace MusicPlayer.Helpers;

public enum ShortcutAction
{
    None,
    PlayPause,
    Next,
    Previous,
    VolumeUp,
    VolumeDown,
    Search,
}

/// <summary>Shortcut keyboard di dalam aplikasi, seperti Apple Music di Windows. Dipisah dari tampilan supaya bisa diuji.</summary>
public static class ShortcutKeys
{
    /// <summary>Langkah volume untuk Ctrl+↑ dan Ctrl+↓.</summary>
    public const double VolumeStep = 5;

    /// <summary>
    /// Aksi untuk satu tombol. Saat sedang mengetik (kotak teks fokus), tidak ada shortcut: Spasi mengetik spasi dan
    /// Ctrl+←/→ memindah kursor per kata seperti biasa.
    /// </summary>
    public static ShortcutAction Resolve(VirtualKey key, bool control, bool typing)
    {
        if (typing)
            return ShortcutAction.None;

        return (key, control) switch
        {
            (VirtualKey.Space, false) => ShortcutAction.PlayPause,
            (VirtualKey.Right, true) => ShortcutAction.Next,
            (VirtualKey.Left, true) => ShortcutAction.Previous,
            (VirtualKey.Up, true) => ShortcutAction.VolumeUp,
            (VirtualKey.Down, true) => ShortcutAction.VolumeDown,
            (VirtualKey.F, true) => ShortcutAction.Search,
            _ => ShortcutAction.None,
        };
    }
}

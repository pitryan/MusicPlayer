using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace MusicPlayer.Helpers;

/// <summary>
/// Memasang shortcut keyboard (lihat ShortcutKeys) di jendela utama, MiniPlayer, dan layar penuh. Tombol media
/// keyboard tidak ditangani di sini, karena Windows sudah meneruskannya lewat kontrol media (WindowsMediaControls).
/// </summary>
public static class PlaybackShortcuts
{
    /// <param name="focusSearch">Ctrl+F: fokus ke kotak pencarian; null untuk jendela tanpa pencarian.</param>
    public static void Attach(UIElement root, Action? focusSearch = null)
    {
        // PreviewKeyDown (dari luar ke dalam), supaya Spasi tetap Play/Pause walaupun ada tombol yang sedang fokus;
        // tanpa ini Spasi akan "mengklik" tombol itu
        root.PreviewKeyDown += (_, e) =>
        {
            bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            bool typing = root.XamlRoot is { } xamlRoot
                && FocusManager.GetFocusedElement(xamlRoot) is TextBox or PasswordBox or RichEditBox;

            var action = ShortcutKeys.Resolve(e.Key, control, typing);
            if (action == ShortcutAction.None || (action == ShortcutAction.Search && focusSearch is null))
                return;

            Execute(action, focusSearch);
            e.Handled = true;
        };
    }

    private static void Execute(ShortcutAction action, Action? focusSearch)
    {
        var playback = App.Playback;
        switch (action)
        {
            case ShortcutAction.PlayPause:
                playback.TogglePlayPause();
                break;
            case ShortcutAction.Next:
                playback.Next();
                break;
            case ShortcutAction.Previous:
                playback.Previous();
                break;
            case ShortcutAction.VolumeUp:
                playback.Volume = Math.Min(100, playback.Volume + ShortcutKeys.VolumeStep);
                break;
            case ShortcutAction.VolumeDown:
                playback.Volume = Math.Max(0, playback.Volume - ShortcutKeys.VolumeStep);
                break;
            case ShortcutAction.Search:
                focusSearch?.Invoke();
                break;
        }
    }
}

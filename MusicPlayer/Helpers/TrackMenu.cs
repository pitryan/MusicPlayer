using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Library;
using Windows.Foundation;

namespace MusicPlayer.Helpers;

/// <summary>Menu klik kanan untuk lagu, dipakai bersama oleh semua halaman yang menampilkan daftar lagu.</summary>
public static class TrackMenu
{
    /// <summary>
    /// Menampilkan menu di posisi kursor. "Play" memakai seluruh daftar sebagai antrean,
    /// sama seperti klik dua kali.
    /// </summary>
    public static void Show(UIElement target, Point position, IReadOnlyList<Track> list, int index)
    {
        if (index < 0 || index >= list.Count)
            return;

        var track = list[index];
        var menu = new MenuFlyout();
        menu.Items.Add(CreateItem("Play", "\uE768", () => App.Playback.PlayQueue(list, index)));
        menu.Items.Add(CreateItem("Play Next", "\uE893", () => App.Playback.PlayNext(track)));
        menu.Items.Add(CreateItem("Add to Queue", "\uE710", () => App.Playback.AddToQueue(track)));
        menu.ShowAt(target, position);
    }

    public static MenuFlyoutItem CreateItem(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => onClick();
        return item;
    }
}

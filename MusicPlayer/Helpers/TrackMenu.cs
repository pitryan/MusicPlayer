using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Library;
using MusicPlayer.Pages;
using Windows.Foundation;

namespace MusicPlayer.Helpers;

/// <summary>Menu klik kanan untuk lagu, dipakai bersama oleh semua halaman yang menampilkan daftar lagu.</summary>
public static class TrackMenu
{
    /// <summary>
    /// Menampilkan menu di posisi kursor. "Play" memakai seluruh daftar sebagai antrean,
    /// sama seperti klik dua kali. Halaman album menyembunyikan "Go to Album", dan halaman artis
    /// menyembunyikan "Go to Artist", karena kamu sudah berada di sana.
    /// </summary>
    public static void Show(UIElement target, Point position, IReadOnlyList<Track> list, int index,
        bool goToAlbum = true, bool goToArtist = true)
    {
        if (index < 0 || index >= list.Count)
            return;

        var track = list[index];
        var menu = new MenuFlyout();
        menu.Items.Add(CreateItem("Play", "\uE768", () => App.Playback.PlayQueue(list, index)));
        menu.Items.Add(CreateItem("Play Next", "\uE893", () => App.Playback.PlayNext(track)));
        menu.Items.Add(CreateItem("Add to Queue", "\uE710", () => App.Playback.AddToQueue(track)));
        AddGoToItems(menu, track, goToAlbum, goToArtist);
        menu.ShowAt(target, position);
    }

    /// <summary>
    /// Menu untuk satu album (tombol ••• di header halaman album): Play Next, Add to Queue, Go to Artist, dan
    /// Show in File Explorer.
    /// </summary>
    public static void ShowForAlbum(UIElement target, Point position, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
            return;

        var menu = new MenuFlyout();

        // Play Next dari lagu terakhir ke pertama: setiap lagu masuk ke depan antrean, jadi urutan album tetap terjaga
        menu.Items.Add(CreateItem("Play Next", "\uE893", () =>
        {
            for (int i = tracks.Count - 1; i >= 0; i--)
                App.Playback.PlayNext(tracks[i]);
        }));
        menu.Items.Add(CreateItem("Add to Queue", "\uE710", () =>
        {
            foreach (var track in tracks)
                App.Playback.AddToQueue(track);
        }));
        AddGoToItems(menu, tracks[0], goToAlbum: false);
        menu.ShowAt(target, position);
    }

    /// <summary>
    /// Menambahkan "Go to Album", "Go to Artist", dan "Show in File Explorer" ke sebuah menu.
    /// Dipakai juga oleh menu di panel antrean, History, dan LCD.
    /// </summary>
    public static void AddGoToItems(MenuFlyout menu, Track track, bool goToAlbum = true, bool goToArtist = true)
    {
        menu.Items.Add(new MenuFlyoutSeparator());

        if (goToAlbum && App.Database.FindAlbum(track) is { } album)
            menu.Items.Add(CreateItem("Go to Album", "\uE93C", () => App.MainWindow.ShowPage(typeof(AlbumDetailPage), album)));

        // Nama artis utama, dengan aturan yang sama seperti halaman Artists (tanpa bagian "feat.")
        string artist = ArtistNames.Primary(track.AlbumArtist ?? track.Artist);
        if (goToArtist && artist.Length > 0)
            menu.Items.Add(CreateItem("Go to Artist", "\uE77B", () => App.MainWindow.ShowPage(typeof(ArtistsPage), artist)));

        menu.Items.Add(CreateItem("Show in File Explorer", "\uE838", () => ShowInExplorer(track.FilePath)));
    }

    public static MenuFlyoutItem CreateItem(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Membuka File Explorer dengan file lagu itu langsung terpilih.</summary>
    private static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // File Explorer gagal dibuka: tidak ada yang bisa dilakukan, aplikasi tetap jalan
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
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
    /// menyembunyikan "Go to Artist", karena kamu sudah berada di sana. Halaman playlist mengisi
    /// <paramref name="removeFromPlaylist"/> untuk menambahkan "Remove from Playlist".
    /// </summary>
    public static void Show(UIElement target, Point position, IReadOnlyList<Track> list, int index,
        bool goToAlbum = true, bool goToArtist = true, Action? removeFromPlaylist = null)
    {
        if (index < 0 || index >= list.Count)
            return;

        var track = list[index];
        var menu = new MenuFlyout();
        menu.Items.Add(CreateItem("Play", "\uE768", () => App.Playback.PlayQueue(list, index)));
        menu.Items.Add(CreateItem("Play Next", "\uE893", () => App.Playback.PlayNext(track)));
        menu.Items.Add(CreateItem("Add to Queue", "\uE710", () => App.Playback.AddToQueue(track)));
        AddPlaylistItem(menu, [track]);
        if (removeFromPlaylist is not null)
            menu.Items.Add(CreateItem("Remove from Playlist", "\uE711", removeFromPlaylist));
        AddFavouriteItem(menu, track);
        AddGoToItems(menu, track, goToAlbum, goToArtist);
        menu.ShowAt(target, position);
    }

    /// <summary>
    /// Submenu "Add to Playlist": New Playlist…, lalu semua playlist. Dipakai untuk satu lagu, satu album, LCD,
    /// antrean, dan History. <paramref name="suggestedName"/> mengisi nama awal playlist baru (misalnya nama album).
    /// </summary>
    public static void AddPlaylistItem(MenuFlyout menu, IReadOnlyList<Track> tracks, string suggestedName = "")
    {
        var submenu = new MenuFlyoutSubItem { Text = "Add to Playlist", Icon = new FontIcon { Glyph = "\uE8D6" } };
        submenu.Items.Add(CreateItem("New Playlist…", "\uE710", () => _ = AddToNewPlaylistAsync(tracks, suggestedName)));

        var playlists = App.Playlists.GetAll();
        if (playlists.Count > 0)
            submenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var playlist in playlists)
            submenu.Items.Add(CreateItem(playlist.Name, "\uE8D6", () => _ = AddToPlaylistAsync(playlist, tracks)));

        menu.Items.Add(submenu);
    }

    /// <summary>Lagu yang sudah ada di playlist itu ditanyakan dulu: tetap ditambahkan, atau dilewati.</summary>
    public static async Task AddToPlaylistAsync(PlaylistSummary playlist, IReadOnlyList<Track> tracks)
    {
        int existing = App.Playlists.CountExisting(playlist.Id, tracks);
        bool skipExisting = existing > 0 && !await Dialogs.AskAddDuplicatesAsync(existing, tracks.Count, playlist.Name);
        App.Playlists.Add(playlist.Id, tracks, skipExisting);
    }

    private static async Task AddToNewPlaylistAsync(IReadOnlyList<Track> tracks, string suggestedName)
    {
        if (await Dialogs.AskNameAsync("New Playlist", "Create", suggestedName) is not { } name)
            return;

        long id = App.Playlists.Create(name);
        App.Playlists.Add(id, tracks, skipExisting: false);
    }

    /// <summary>
    /// "Favourite" atau "Remove from Favourites", tergantung status lagu itu sekarang. Dipakai juga oleh menu LCD,
    /// panel antrean, dan History.
    /// </summary>
    public static void AddFavouriteItem(MenuFlyout menu, Track track)
    {
        bool favourite = App.Favourites.IsFavourite(track);
        menu.Items.Add(CreateItem(
            favourite ? "Remove from Favourites" : "Favourite",
            favourite ? "\uE735" : "\uE734",
            () => App.Favourites.Set(track, !favourite)));
    }

    /// <summary>
    /// Menu untuk satu album (tombol ••• di header halaman album): Play Next, Add to Queue, Add to Playlist,
    /// Go to Artist, dan Show in File Explorer.
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
        AddPlaylistItem(menu, tracks, tracks[0].DisplayAlbum);
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

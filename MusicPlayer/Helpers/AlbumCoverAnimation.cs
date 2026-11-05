using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using MusicPlayer.Library;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Helpers;

/// <summary>
/// Cover album yang "terbang" dari kotak yang diklik ke tempatnya di halaman album (connected animation), dan kembali ke
/// kotaknya di grid Albums saat Back. Selama cover terbang, transisi halaman biasa dimatikan supaya keduanya tidak saling
/// menimpa; sisa halaman album memudar masuk. Kalau animasi Windows dimatikan, halaman album dibuka dengan transisi biasa.
/// </summary>
/// <remarks>
/// Back hanya terbang ke grid Albums (Albums, Recently Added, Recently Played, genre), karena halaman itu di-cache sehingga
/// kotaknya masih ada. Halaman lain dibuat ulang saat Back, jadi kembali tanpa animasi cover.
/// </remarks>
public static class AlbumCoverAnimation
{
    /// <summary>Nama elemen cover di template kotak album sebuah GridView (lihat <see cref="Open(ListViewBase, object)"/>).</summary>
    public const string CoverElementName = "CoverTile";

    private const string OpenKey = "AlbumCoverOpen";
    private const string BackKey = "AlbumCoverBack";

    // Album yang cover-nya sedang terbang kembali (lihat PrepareBack dan Return)
    private static AlbumSummary? _returning;

    private static ConnectedAnimationService Service => ConnectedAnimationService.GetForCurrentView();

    /// <summary>Kotak di GridView yang diklik: cover-nya disiapkan untuk terbang. Hasilnya transisi untuk Frame.Navigate.</summary>
    public static NavigationTransitionInfo Open(ListViewBase grid, object item)
    {
        if (!Animations.Enabled)
            return Animations.DrillIn;
        grid.PrepareConnectedAnimation(OpenKey, item, CoverElementName);
        return new SuppressNavigationTransitionInfo();
    }

    /// <summary>Cover yang diklik di luar GridView (rak album, Latest Release). Hasilnya transisi untuk Frame.Navigate.</summary>
    public static NavigationTransitionInfo Open(UIElement cover)
    {
        if (!Animations.Enabled)
            return Animations.DrillIn;
        Service.PrepareToAnimate(OpenKey, cover);
        return new SuppressNavigationTransitionInfo();
    }

    /// <summary>
    /// Halaman album: cover yang terbang mendarat di <paramref name="cover"/>, dan <paramref name="content"/> memudar masuk.
    /// False kalau tidak ada cover yang terbang (halaman dibuka dari tempat lain, dengan transisi biasa).
    /// </summary>
    public static bool Land(UIElement cover, UIElement content)
    {
        if (Service.GetAnimation(OpenKey) is not { } animation)
            return false;

        // Konfigurasi bawaan ("gravity"): cover sedikit terangkat dengan bayangan selama terbang
        animation.TryStart(cover);
        Animations.FadeIn(content, 250);
        return true;
    }

    /// <summary>Halaman album ditutup dengan Back ke grid Albums: cover-nya disiapkan untuk terbang ke kotak album itu.</summary>
    public static void PrepareBack(UIElement cover, AlbumSummary album)
    {
        if (!Animations.Enabled)
            return;
        _returning = album;
        Service.PrepareToAnimate(BackKey, cover);
    }

    /// <summary>
    /// Grid Albums setelah Back: cover yang disiapkan PrepareBack mendarat di kotak albumnya. Dibatalkan kalau album itu tidak
    /// ada di grid (misalnya grid genre lain, atau album-nya sudah tidak ada setelah tag diedit).
    /// </summary>
    public static async void Return(ListViewBase grid)
    {
        var album = _returning;
        _returning = null;
        if (Service.GetAnimation(BackKey) is not { } animation)
            return;

        var item = album is null ? null : grid.Items.OfType<AlbumItem>().FirstOrDefault(i =>
            string.Equals(i.Summary.Title, album.Title, StringComparison.Ordinal)
            && string.Equals(i.Summary.Artist, album.Artist, StringComparison.Ordinal));
        if (item is null)
        {
            animation.Cancel();
            return;
        }

        // Kembali ke kotaknya secara langsung, tanpa efek terangkat
        animation.Configuration = new DirectConnectedAnimationConfiguration();
        grid.ScrollIntoView(item);
        grid.UpdateLayout();
        if (!await grid.TryStartConnectedAnimationAsync(animation, item, CoverElementName))
            animation.Cancel();
    }
}

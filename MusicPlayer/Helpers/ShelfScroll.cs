using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicPlayer.Helpers;

/// <summary>
/// Rak mendatar dengan panah kiri/kanan seperti Apple Music (More by, rak di halaman artis, Top Songs). Panah kiri hanya
/// muncul kalau ada yang bisa digulir ke kiri, panah kanan hanya kalau masih ada di kanan. Scrollbar-nya disembunyikan
/// di XAML; rak tetap bisa digulir dengan Shift+roda mouse atau touchpad.
/// </summary>
public static class ShelfScroll
{
    /// <summary>Menghubungkan rak dengan panahnya. Dipanggil sekali, dari konstruktor halaman atau kontrolnya.</summary>
    public static void Attach(ScrollViewer scroller, FrameworkElement content, Button left, Button right)
    {
        void Update()
        {
            var (showLeft, showRight) = ShelfMath.Arrows(scroller.HorizontalOffset, scroller.ScrollableWidth);
            left.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
            right.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        }

        scroller.ViewChanged += (_, _) => Update();
        scroller.SizeChanged += (_, _) => Update();
        content.SizeChanged += (_, _) => Update();
        left.Click += (_, _) => scroller.ChangeView(ShelfMath.Target(scroller.HorizontalOffset, scroller.ViewportWidth, scroller.ScrollableWidth, -1), null, null);
        right.Click += (_, _) => scroller.ChangeView(ShelfMath.Target(scroller.HorizontalOffset, scroller.ViewportWidth, scroller.ScrollableWidth, 1), null, null);
    }
}

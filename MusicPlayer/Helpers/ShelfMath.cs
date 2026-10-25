using System;

namespace MusicPlayer.Helpers;

/// <summary>Hitungan rak mendatar (lihat ShelfScroll), dipisah supaya bisa diuji tanpa WinUI.</summary>
public static class ShelfMath
{
    // Per klik digulir 80% lebar yang terlihat, supaya satu-dua kotak terakhir masih terlihat sebagai patokan
    public const double PageFraction = 0.8;

    /// <summary>Panah kiri hanya kalau ada yang bisa digulir ke kiri, panah kanan hanya kalau masih ada di kanan.</summary>
    public static (bool Left, bool Right) Arrows(double offset, double scrollableWidth) =>
        (offset > 1, offset < scrollableWidth - 1);

    /// <summary>Posisi tujuan satu klik panah: direction -1 ke kiri, +1 ke kanan; tidak pernah melewati ujung.</summary>
    public static double Target(double offset, double viewportWidth, double scrollableWidth, int direction) =>
        Math.Clamp(offset + direction * viewportWidth * PageFraction, 0, Math.Max(0, scrollableWidth));
}

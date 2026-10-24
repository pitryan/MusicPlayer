using System;

namespace MusicPlayer.Helpers;

/// <summary>Menjaga jendela tetap di dalam layar saat ukurannya berubah. Dipisah dari tampilan supaya bisa diuji.</summary>
public static class WindowFit
{
    /// <summary>
    /// Posisi atas jendela setelah tingginya berubah: tetap di tempatnya (memanjang ke bawah) kalau masih muat di area
    /// kerja layar; kalau tidak, naik secukupnya, tapi tidak melewati tepi atas layar.
    /// </summary>
    public static int Top(int top, int height, int workTop, int workHeight) =>
        top + height <= workTop + workHeight ? top : Math.Max(workTop, workTop + workHeight - height);
}

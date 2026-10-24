using System;
using Microsoft.UI.Xaml.Controls;

namespace MusicPlayer.Helpers;

/// <summary>
/// Grid kotak (album, playlist) seperti Apple Music: jarak antar kotak 20 px, dan jumlah kolom menyesuaikan lebar
/// sehingga kotak-kotaknya mengisi penuh lebar baris (sekitar 240 px di jendela 1920 px, 6 per baris).
/// </summary>
public static class TileGrid
{
    public const double Gap = 20;

    // Ukuran kotak terkecil sebelum jumlah kolom berkurang satu
    public const double MinTile = 210;

    // Judul dan artis di bawah kotak (masing-masing satu baris), dengan jaraknya dari kotak
    public const double TextHeight = 46;

    /// <summary>
    /// Jumlah kolom, lebar sel (kotak beserta jarak di kanannya), dan lebar kotak untuk lebar yang tersedia; paling
    /// sedikit 2 kolom. Jumlah lebar sel tidak pernah melebihi lebar yang tersedia: kalau melebihi walau sedikit,
    /// kotak terakhir turun ke baris berikutnya dan meninggalkan sela kosong di kanan.
    /// </summary>
    public static (int Columns, double Cell, double Tile) Layout(double availableWidth)
    {
        int columns = Math.Max(2, (int)(availableWidth / (MinTile + Gap)));
        double cell = Math.Floor(availableWidth / columns);
        return (columns, cell, Math.Max(cell - Gap, 80));
    }

    /// <summary>Menyetel ukuran kotak GridView mengikuti lebarnya. Dipanggil saat ukuran grid berubah dan saat dimuat.</summary>
    public static void Fit(GridView grid)
    {
        if (grid.ItemsPanelRoot is not ItemsWrapGrid panel)
            return;
        double available = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right;
        if (available <= 0)
            return;

        var (_, cell, tile) = Layout(available);
        // Setiap kotak membawa jaraknya sendiri di kanan dan bawah (lihat Margin di template)
        panel.ItemWidth = cell;
        panel.ItemHeight = tile + TextHeight + Gap;
    }
}

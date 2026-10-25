namespace MusicPlayer.Library;

/// <summary>Cara menanam cover yang tidak persegi (halaman Change Artwork).</summary>
public enum CoverShape
{
    /// <summary>Apa adanya.</summary>
    Original,

    /// <summary>Dipotong di tengah menjadi persegi; sisi yang berlebih dibuang rata kiri-kanan atau atas-bawah.</summary>
    Crop,

    /// <summary>Gambar utuh di tengah kanvas persegi; sisanya diisi warna tepi gambar supaya menyatu.</summary>
    Fit,
}

/// <summary>
/// Membuat cover menjadi persegi, langsung pada piksel BGRA (4 byte per piksel, baris demi baris). Dekode dan enkode
/// gambarnya dikerjakan aplikasi (Windows.Graphics.Imaging); di sini hanya hitungannya, supaya bisa diuji.
/// </summary>
public static class CoverSquare
{
    /// <summary>Potongan persegi di tengah: sisinya sama dengan sisi terpendek gambar.</summary>
    public static (int X, int Y, int Side) CropRect(int width, int height)
    {
        int side = Math.Min(width, height);
        return ((width - side) / 2, (height - side) / 2, side);
    }

    /// <summary>Kanvas persegi untuk Fit: sisinya sama dengan sisi terpanjang, gambar di tengahnya.</summary>
    public static (int Side, int OffsetX, int OffsetY) FitLayout(int width, int height)
    {
        int side = Math.Max(width, height);
        return (side, (side - width) / 2, (side - height) / 2);
    }

    public static byte[] Apply(CoverShape shape, byte[] bgra, int width, int height, out int side)
    {
        switch (shape)
        {
            case CoverShape.Crop:
                return Crop(bgra, width, height, out side);
            case CoverShape.Fit:
                return Fit(bgra, width, height, out side);
            default:
                side = Math.Max(width, height);
                return bgra;
        }
    }

    public static byte[] Crop(byte[] bgra, int width, int height, out int side)
    {
        var (x, y, s) = CropRect(width, height);
        side = s;
        var result = new byte[s * s * 4];
        for (int row = 0; row < s; row++)
            Buffer.BlockCopy(bgra, ((y + row) * width + x) * 4, result, row * s * 4, s * 4);
        return result;
    }

    /// <summary>
    /// Gambar utuh di tengah kanvas persegi. Gambar yang melebar mendapat bingkai di atas dan bawah, diisi rata-rata warna
    /// baris paling atas dan paling bawahnya; gambar yang meninggi mendapat bingkai kiri dan kanan dari kolom tepinya.
    /// </summary>
    public static byte[] Fit(byte[] bgra, int width, int height, out int side)
    {
        var (s, offsetX, offsetY) = FitLayout(width, height);
        side = s;
        var result = new byte[s * s * 4];

        if (width >= height)
        {
            var top = Average(bgra, width, Enumerable.Range(0, width).Select(x => (x, 0)));
            var bottom = Average(bgra, width, Enumerable.Range(0, width).Select(x => (x, height - 1)));
            Fill(result, s, 0, 0, s, offsetY, top);
            Fill(result, s, 0, offsetY + height, s, s - offsetY - height, bottom);
        }
        else
        {
            var left = Average(bgra, width, Enumerable.Range(0, height).Select(y => (0, y)));
            var right = Average(bgra, width, Enumerable.Range(0, height).Select(y => (width - 1, y)));
            Fill(result, s, 0, 0, offsetX, s, left);
            Fill(result, s, offsetX + width, 0, s - offsetX - width, s, right);
        }

        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(bgra, row * width * 4, result, ((offsetY + row) * s + offsetX) * 4, width * 4);
        return result;
    }

    private static (byte B, byte G, byte R) Average(byte[] bgra, int width, IEnumerable<(int X, int Y)> pixels)
    {
        long b = 0, g = 0, r = 0, count = 0;
        foreach (var (x, y) in pixels)
        {
            int i = (y * width + x) * 4;
            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
            count++;
        }
        return count == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)(b / count), (byte)(g / count), (byte)(r / count));
    }

    private static void Fill(byte[] canvas, int side, int x, int y, int width, int height, (byte B, byte G, byte R) color)
    {
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                int i = (row * side + column) * 4;
                canvas[i] = color.B;
                canvas[i + 1] = color.G;
                canvas[i + 2] = color.R;
                canvas[i + 3] = 255;
            }
        }
    }
}

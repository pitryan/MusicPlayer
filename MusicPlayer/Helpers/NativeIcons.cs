using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MusicPlayer.Helpers;

public enum TransportIcon
{
    Previous,
    Play,
    Pause,
    Next,
}

/// <summary>
/// Ikon Win32 (HICON) untuk tombol di thumbnail taskbar dan ikon di notification area. Ikon transport digambar sendiri
/// dari bentuk sederhana (segitiga dan batang), supaya tajam di setiap skala layar tanpa bergantung pada font ikon.
/// Setiap ikon harus dibuang dengan <see cref="Destroy"/>.
/// </summary>
internal static class NativeIcons
{
    // Bentuk di kotak 20×20, seperti ikon Fluent 20 px
    private const float Grid = 20f;

    private static readonly float[][] PlayShapes = [[5.5f, 3.5f, 5.5f, 16.5f, 16.5f, 10f]];
    private static readonly float[][] PauseShapes = [Rect(5f, 3.5f, 8.5f, 16.5f), Rect(11.5f, 3.5f, 15f, 16.5f)];
    private static readonly float[][] NextShapes = [[3.5f, 4f, 3.5f, 16f, 12.5f, 10f], Rect(13.5f, 4f, 16f, 16f)];
    private static readonly float[][] PreviousShapes = [[16.5f, 4f, 16.5f, 16f, 7.5f, 10f], Rect(4f, 4f, 6.5f, 16f)];

    private static float[] Rect(float left, float top, float right, float bottom) =>
        [left, top, right, top, right, bottom, left, bottom];

    /// <summary>Ikon putih berukuran <paramref name="size"/> piksel.</summary>
    public static IntPtr Transport(TransportIcon icon, int size)
    {
        var shapes = icon switch
        {
            TransportIcon.Previous => PreviousShapes,
            TransportIcon.Pause => PauseShapes,
            TransportIcon.Next => NextShapes,
            _ => PlayShapes,
        };

        // 4×4 titik per piksel, supaya tepi miringnya halus
        const int samples = 4;
        var pixels = new int[size * size];
        float scale = Grid / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        float px = (x + (sx + 0.5f) / samples) * scale;
                        float py = (y + (sy + 0.5f) / samples) * scale;
                        foreach (var shape in shapes)
                        {
                            if (Inside(shape, px, py))
                            {
                                covered++;
                                break;
                            }
                        }
                    }
                }
                int alpha = covered * 255 / (samples * samples);
                pixels[y * size + x] = (alpha << 24) | 0x00FFFFFF;
            }
        }
        return FromPixels(pixels, size);
    }

    /// <summary>Di dalam poligon cembung (x1, y1, x2, y2, …): semua sisinya ada di satu sisi titik itu.</summary>
    private static bool Inside(float[] polygon, float x, float y)
    {
        int count = polygon.Length / 2;
        bool positive = false, negative = false;
        for (int i = 0; i < count; i++)
        {
            float x1 = polygon[i * 2], y1 = polygon[i * 2 + 1];
            float x2 = polygon[(i + 1) % count * 2], y2 = polygon[(i + 1) % count * 2 + 1];
            float cross = (x2 - x1) * (y - y1) - (y2 - y1) * (x - x1);
            positive |= cross > 0;
            negative |= cross < 0;
            if (positive && negative)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Logo aplikasi untuk notification area, dari PNG di folder Assets (ukuran terdekat yang tidak lebih kecil).
    /// Ikon aplikasi bawaan Windows kalau file-nya tidak bisa dibaca.
    /// </summary>
    public static IntPtr AppLogo(int size)
    {
        int[] available = [16, 24, 32, 48, 256];
        int chosen = Array.Find(available, s => s >= size);
        if (chosen == 0)
            chosen = 256;
        try
        {
            byte[] png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets",
                $"Square44x44Logo.targetsize-{chosen}_altform-unplated.png"));
            IntPtr icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
            if (icon != IntPtr.Zero)
                return icon;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return CopyIcon(LoadIcon(IntPtr.Zero, IDI_APPLICATION));
    }

    public static void Destroy(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
            DestroyIcon(icon);
    }

    /// <summary>Piksel ARGB (alpha tidak dikalikan ke warnanya, seperti file .ico 32-bit) menjadi HICON.</summary>
    private static IntPtr FromPixels(int[] pixels, int size)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // dari atas ke bawah
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero)
            return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        // Mask monokrom kosong: transparansinya diambil dari alpha. Tiap baris dibulatkan ke 2 byte.
        byte[] maskBits = new byte[(size + 15) / 16 * 2 * size];
        IntPtr mask = CreateBitmap(size, size, 1, 1, maskBits);
        var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(mask);
        DeleteObject(color);
        return icon;
    }

    private static readonly IntPtr IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint length, bool icon, uint version, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

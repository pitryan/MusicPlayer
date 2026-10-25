namespace MusicPlayer.Library;

/// <summary>
/// Ukuran gambar (piksel) dibaca dari header JPEG atau PNG, tanpa mendekode gambarnya. Dipakai halaman Change Artwork
/// untuk menampilkan ukuran cover dan memberi tahu kalau covernya tidak persegi.
/// </summary>
public static class ImageInfo
{
    public static (int Width, int Height)? Dimensions(byte[] data)
    {
        // PNG: tanda tangan 8 byte, lalu chunk IHDR berisi lebar dan tinggi (big-endian)
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return (BigEndian32(data, 16), BigEndian32(data, 20));

        // JPEG: telusuri segmen sampai SOF (start of frame), yang berisi tinggi lalu lebar
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return null;
        int offset = 2;
        while (offset + 9 < data.Length)
        {
            if (data[offset] != 0xFF)
                return null;
            byte marker = data[offset + 1];
            if (marker == 0xFF)
            {
                offset++; // byte pengisi
                continue;
            }
            int length = (data[offset + 2] << 8) | data[offset + 3];
            // SOF0–SOF15, kecuali DHT (C4), JPG (C8), dan DAC (CC) yang memakai nomor di rentang yang sama
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return ((data[offset + 7] << 8) | data[offset + 8], (data[offset + 5] << 8) | data[offset + 6]);
            if (length < 2)
                return null;
            offset += 2 + length;
        }
        return null;
    }

    /// <summary>Persegi kalau sisi panjang paling banyak 1% lebih panjang dari sisi pendek.</summary>
    public static bool IsSquare(int width, int height) =>
        width > 0 && height > 0 && Math.Max(width, height) <= Math.Min(width, height) * 1.01;

    private static int BigEndian32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Library;
using Windows.Storage.Streams;

namespace MusicPlayer.Services;

/// <summary>Mengubah cover sebuah lagu menjadi gambar yang bisa ditampilkan.</summary>
public static class CoverImageLoader
{
    /// <summary>
    /// Membaca cover di thread latar (supaya UI tidak tersendat), lalu men-decode gambarnya
    /// hanya selebar yang dibutuhkan. Harus dipanggil dari UI thread.
    /// </summary>
    public static async Task<BitmapImage?> LoadAsync(string trackPath, int decodePixelWidth)
    {
        byte[]? data = await Task.Run(() => CoverArt.TryRead(trackPath));
        return data is null ? null : await FromBytesAsync(data, decodePixelWidth);
    }

    /// <summary>Gambar dari isi file gambar (JPEG/PNG), misalnya cover di editor tag. Harus dipanggil dari UI thread.</summary>
    public static async Task<BitmapImage?> FromBytesAsync(byte[] data, int decodePixelWidth)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);

            var image = new BitmapImage { DecodePixelWidth = decodePixelWidth };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            return null; // gambar rusak atau formatnya tidak didukung
        }
    }
}

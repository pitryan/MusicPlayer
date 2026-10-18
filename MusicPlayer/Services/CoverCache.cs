using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;

namespace MusicPlayer.Services;

/// <summary>
/// Menyimpan cover yang sudah dimuat, supaya grid album tidak membaca ulang file yang sama
/// setiap kali di-scroll atau saat kembali dari halaman detail. Semua aksesnya dari UI thread.
/// </summary>
public static class CoverCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    // Batasi pembacaan cover yang berjalan bersamaan, supaya disk tidak kewalahan saat scroll cepat
    private static readonly SemaphoreSlim Gate = new(4);

    public static async Task<ImageSource?> GetAsync(string trackPath, int decodePixelWidth)
    {
        string key = $"{decodePixelWidth}|{trackPath}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        await Gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(key, out cached))
                return cached;

            ImageSource? image = await CoverImageLoader.LoadAsync(trackPath, decodePixelWidth);
            Cache[key] = image; // null juga disimpan: lagu tanpa cover tidak dicari ulang
            return image;
        }
        finally
        {
            Gate.Release();
        }
    }
}

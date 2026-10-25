using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using MusicPlayer.Library;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MusicPlayer.Helpers;

/// <summary>
/// Membuat cover menjadi persegi (Crop atau Fit) dengan dekoder dan enkoder gambar bawaan Windows; hitungan pikselnya
/// di CoverSquare. JPEG tetap JPEG dan PNG tetap PNG.
/// </summary>
public static class CoverReshaper
{
    // Kualitas JPEG hasilnya: cukup tinggi supaya tidak terlihat bedanya dengan aslinya
    private const double JpegQuality = 0.92;

    public static async Task<TagPicture> ReshapeAsync(TagPicture source, CoverShape shape)
    {
        if (shape == CoverShape.Original)
            return source;

        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.Data.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        int width = (int)decoder.PixelWidth;
        int height = (int)decoder.PixelHeight;
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] square = CoverSquare.Apply(shape, pixels.DetachPixelData(), width, height, out int side);

        bool png = source.MimeType == "image/png";
        using var output = new InMemoryRandomAccessStream();
        var encoder = png
            ? await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output)
            : await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
                new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(JpegQuality, PropertyType.Single) });
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)side, (uint)side, decoder.DpiX, decoder.DpiY, square);
        await encoder.FlushAsync();

        output.Seek(0);
        using var copy = new MemoryStream();
        await output.AsStreamForRead().CopyToAsync(copy);
        return source with { Data = copy.ToArray() };
    }
}

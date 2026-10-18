namespace MusicPlayer.Library;

/// <summary>
/// Mengambil gambar cover sebuah lagu: dari tag lebih dulu, kalau tidak ada
/// dari file gambar di folder albumnya (cover.jpg, folder.jpg, dan sebagainya).
/// </summary>
public static class CoverArt
{
    private static readonly string[] FolderImageNames = ["cover", "folder", "front", "album"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Mengembalikan isi file gambar, atau null kalau lagu ini tidak punya cover.</summary>
    public static byte[]? TryRead(string trackPath)
    {
        try
        {
            using var file = TagLib.File.Create(trackPath);
            var pictures = file.Tag.Pictures;
            if (pictures is { Length: > 0 })
            {
                var front = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
                return front.Data.Data;
            }
        }
        catch (Exception)
        {
            // Tag tidak bisa dibaca: lanjut mencari gambar di folder
        }

        try
        {
            return ReadFolderImage(Path.GetDirectoryName(trackPath));
        }
        catch (Exception)
        {
            return null; // folder tidak bisa dibaca
        }
    }

    private static byte[]? ReadFolderImage(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
            return null;

        // 1. Nama yang umum dipakai untuk cover album
        foreach (string name in FolderImageNames)
        {
            foreach (string extension in ImageExtensions)
            {
                string candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);
            }
        }

        // 2. Kalau di folder itu hanya ada satu gambar, hampir pasti itu cover-nya
        var images = Directory.EnumerateFiles(folder)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Take(2)
            .ToList();

        return images.Count == 1 ? File.ReadAllBytes(images[0]) : null;
    }
}

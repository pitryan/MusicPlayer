using System.Text;
using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>FLAC dengan ruang kosong (padding) besar di metadata-nya, biasanya bekas cover lama.</summary>
public sealed record PaddingItem(string Path, long Bytes);

/// <summary>File di folder musik yang tidak dipakai lagi, dan bisa dipindah ke Recycle Bin.</summary>
public sealed record SpareFileItem(string Path, long Bytes);

/// <summary>
/// File .lrc yang namanya tidak cocok dengan lagu mana pun, jadi liriknya tidak pernah tampil. <see cref="RenameTo"/>:
/// nama baru supaya cocok dengan lagu bernomor track sama di folder itu; null kalau folder itu tidak punya lagu
/// bernomor itu.
/// </summary>
public sealed record LyricFileItem(string Path, string? RenameTo);

/// <summary>Hasil <see cref="LibraryHealth.Scan"/>, masing-masing urut menurut path.</summary>
public sealed record HealthReport(
    IReadOnlyList<PaddingItem> Padding,
    IReadOnlyList<SpareFileItem> SpareImages,
    IReadOnlyList<LyricFileItem> LyricFiles,
    IReadOnlyList<SpareFileItem> Leftovers);

/// <summary>
/// Library Health: mencari yang tidak diperlukan lagi di folder musik, tanpa mengubah lagu dan artwork-nya. Empat
/// jenis: padding FLAC yang besar, gambar cover di folder yang semua lagunya sudah punya cover tertanam, file .lrc yang
/// namanya tidak cocok dengan lagunya, dan file backup atau salinan sementara yang tertinggal.
/// </summary>
public static class LibraryHealth
{
    /// <summary>
    /// Padding sampai segini dibiarkan: tagger seperti foobar2000 dan Picard sengaja menyisakan sedikit ruang supaya
    /// edit tag berikutnya tidak perlu menulis ulang seluruh file. Yang lebih besar biasanya bekas cover yang diganti.
    /// </summary>
    public const long PaddingThreshold = 64 * 1024;

    // Salinan sementara dari TagEditor (".<guid>.tmp") yang lebih muda dari ini mungkin sedang ditulis
    private static readonly TimeSpan TemporaryAge = TimeSpan.FromHours(1);

    private static readonly Regex TemporaryName = new(@"^\.[0-9a-f]{32}\.tmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Nomor track di awal nama file: "01 Title", "1. Title", "03-Title"
    private static readonly Regex LeadingNumber = new(@"^\s*(\d{1,3})(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>Memeriksa semua folder di bawah folder library. Membaca disk, jadi panggil di luar UI thread.</summary>
    public static HealthReport Scan(IEnumerable<string> libraryFolders, CancellationToken ct = default)
    {
        var padding = new List<PaddingItem>();
        var images = new List<SpareFileItem>();
        var lyrics = new List<LyricFileItem>();
        var leftovers = new List<SpareFileItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (string root in libraryFolders)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (string folder in Directory.EnumerateDirectories(root, "*", folderOptions).Prepend(root))
            {
                ct.ThrowIfCancellationRequested();
                // Folder library yang ada di dalam folder library lain cukup diperiksa sekali
                if (seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))))
                    ScanFolder(folder, padding, images, lyrics, leftovers);
            }
        }

        static IReadOnlyList<T> Sorted<T>(List<T> items, Func<T, string> path) =>
            items.OrderBy(path, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new HealthReport(Sorted(padding, p => p.Path), Sorted(images, i => i.Path),
            Sorted(lyrics, l => l.Path), Sorted(leftovers, l => l.Path));
    }

    private static void ScanFolder(string folder, List<PaddingItem> padding, List<SpareFileItem> images,
        List<LyricFileItem> lyrics, List<SpareFileItem> leftovers)
    {
        List<FileInfo> files;
        try
        {
            // File tersembunyi ikut dibaca, karena salinan sementara TagEditor tersembunyi
            files = new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // folder baru saja dihapus atau tidak boleh dibaca
        }

        var visible = files.Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)).ToList();
        var audio = visible.Where(f => AudioFiles.IsAudioFile(f.FullName)).ToList();

        foreach (var file in files)
        {
            if (file.Extension.Equals(".bak", StringComparison.OrdinalIgnoreCase)
                || (TemporaryName.IsMatch(file.Name) && file.LastWriteTimeUtc < DateTime.UtcNow - TemporaryAge))
                leftovers.Add(new SpareFileItem(file.FullName, file.Length));
        }

        foreach (var file in audio.Where(f => f.Extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                long bytes = FlacArtwork.PaddingBytes(file.FullName);
                if (bytes > PaddingThreshold)
                    padding.Add(new PaddingItem(file.FullName, bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // File yang tidak bisa dibaca atau metadata-nya rusak tidak disentuh
            }
        }

        // Gambar di folder hanya dipakai untuk lagu yang tidak punya cover di tag-nya (CoverArt.TryRead)
        var folderImages = visible.Where(f => CoverArt.ImageExtensions.Contains(f.Extension.ToLowerInvariant())).ToList();
        if (folderImages.Count > 0 && audio.Count > 0 && audio.All(f => CoverArt.HasEmbedded(f.FullName)))
            images.AddRange(folderImages.Select(f => new SpareFileItem(f.FullName, f.Length)));

        FindUnmatchedLyrics(folder, audio, visible, lyrics);
    }

    /// <summary>
    /// File .lrc dipakai kalau namanya sama dengan lagunya (LyricsFinder.FindLrcFile). Yang tidak sama dicocokkan lewat
    /// nomor track, hanya kalau hasilnya pasti: tepat satu lagu bernomor itu yang belum punya .lrc, dan tidak ada .lrc
    /// lain yang juga tidak cocok dengan nomor yang sama. Yang tidak pasti tidak dicantumkan.
    /// </summary>
    private static void FindUnmatchedLyrics(string folder, List<FileInfo> audio, List<FileInfo> visible, List<LyricFileItem> lyrics)
    {
        var lrcFiles = visible.Where(f => f.Extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lrcFiles.Count == 0)
            return;

        var songNames = new HashSet<string>(audio.Select(f => NameKey(f.Name)), StringComparer.OrdinalIgnoreCase);
        var lrcNames = new HashSet<string>(lrcFiles.Select(f => NameKey(f.Name)), StringComparer.OrdinalIgnoreCase);
        var unmatched = lrcFiles.Where(f => !songNames.Contains(NameKey(f.Name))).ToList();
        var songsWithoutLrc = audio.Where(f => !lrcNames.Contains(NameKey(f.Name))).ToList();
        bool allSongsNumbered = audio.Count > 0 && audio.All(f => NumberOf(f.Name) is not null);

        foreach (var lrc in unmatched)
        {
            if (NumberOf(lrc.Name) is not { } number)
                continue;

            var songs = songsWithoutLrc.Where(f => NumberOf(f.Name) == number).ToList();
            int rivals = unmatched.Count(f => NumberOf(f.Name) == number);
            if (songs.Count == 1 && rivals == 1)
                lyrics.Add(new LyricFileItem(lrc.FullName,
                    Path.Combine(folder, Path.GetFileNameWithoutExtension(songs[0].Name) + ".lrc")));
            // Lagu di folder ini bernomor semua, dan tidak ada yang bernomor ini: liriknya milik lagu yang tidak ada di sini
            else if (allSongsNumbered && audio.All(f => NumberOf(f.Name) != number))
                lyrics.Add(new LyricFileItem(lrc.FullName, null));
        }
    }

    // Sama dengan LyricsFinder: nama tanpa ekstensi, dirapikan ke bentuk NFC (nama file dari Mac bisa memecah "ド")
    private static string NameKey(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Normalize(NormalizationForm.FormC);

    private static int? NumberOf(string fileName) =>
        LeadingNumber.Match(fileName) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

    /// <summary>
    /// Membuang padding sebuah FLAC. Salinan tanpa padding ditulis lebih dulu, lalu dibandingkan dengan aslinya: semua
    /// selain padding (metadata, cover, dan audio) harus sama byte per byte sebelum salinan itu menggantikan aslinya.
    /// </summary>
    public static void RemovePadding(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"\"{Path.GetFileName(path)}\" is read-only.");

        TagEditor.ReplaceViaTemporary(path, temporary =>
        {
            FlacArtwork.CopyWithoutPadding(path, temporary);
            if (OperatingSystem.IsWindows())
                File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
            FlacArtwork.CheckReadable(temporary);
            if (!FlacArtwork.SameExceptPadding(path, temporary))
                throw new InvalidDataException("The cleaned copy didn't match the original. The original file has not been changed.");
        });
    }

    /// <summary>
    /// Membereskan satu file lirik: diganti namanya supaya cocok dengan lagunya, atau diserahkan ke
    /// <paramref name="discard"/> (Recycle Bin) kalau lagunya tidak ada. Nama tujuan yang ternyata sudah dipakai tidak
    /// ditimpa.
    /// </summary>
    public static void FixLyricFile(LyricFileItem item, Action<string> discard)
    {
        if (item.RenameTo is { } target)
            File.Move(item.Path, target, overwrite: false);
        else
            discard(item.Path);
    }
}

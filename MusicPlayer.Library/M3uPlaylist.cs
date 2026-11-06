using System.Text;
using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>Hasil impor satu file M3U: nama playlist, lagu library yang ditemukan (berurutan), dan path yang tidak ditemukan.</summary>
/// <param name="FoundElsewhere">Berapa lagu yang ditemukan di lokasi lain (misalnya drive lain), lewat nama file dan foldernya.</param>
public sealed record M3uImport(string Name, IReadOnlyList<Track> Tracks, IReadOnlyList<string> Missing, int FoundElsewhere);

/// <summary>
/// Hasil ekspor M3U: teksnya, berapa lagu yang ditulis dengan path relatif (ditemukan di folder tempat playlist disimpan),
/// dan berapa yang ditulis dengan path lengkap.
/// </summary>
public sealed record M3uExport(string Text, int Relative, int Absolute);

/// <summary>
/// Playlist M3U dan M3U8, untuk bertukar playlist dengan aplikasi lain (foobar2000, MusicBee, VLC, Winamp). Isi file M3U
/// hanyalah daftar path, satu per baris; baris yang diawali # adalah komentar atau keterangan (#EXTM3U, #EXTINF).
/// </summary>
public static partial class M3uPlaylist
{
    // File M3U yang lebih besar dari ini hampir pasti bukan playlist (16 MB cukup untuk ratusan ribu lagu)
    private const long MaxBytes = 16 * 1024 * 1024;

    // Alamat dengan skema seperti http:// atau https:// (radio internet), bukan file di komputer ini
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]+://")]
    private static partial Regex UrlScheme();

    /// <summary>
    /// Membaca file M3U/M3U8 dan mencocokkan isinya dengan lagu di library (lihat <see cref="Match"/>). M3U8 selalu UTF-8,
    /// tapi file .m3u lama sering memakai encoding Windows seperti Shift-JIS. Encoding-nya tidak ditebak dari isi teks
    /// (path "創作\春泥棒.flac" tidak punya kana yang menandakan bahasa Jepang), tapi dipilih yang path-nya paling banyak
    /// ditemukan di library; kalau sama banyak, yang paling umum (UTF-8 lebih dulu).
    /// </summary>
    /// <exception cref="IOException">File tidak bisa dibaca, atau terlalu besar untuk sebuah playlist.</exception>
    public static M3uImport Import(string path, IReadOnlyList<Track> library)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            throw new IOException("The file is too large to be a playlist.");

        M3uImport? best = null;
        foreach (string text in TextFiles.DecodeCandidates(File.ReadAllBytes(path)))
        {
            var (tracks, missing, elsewhere) = Match(ParsePaths(text, info.DirectoryName ?? ""), library);
            if (best is null || tracks.Count > best.Tracks.Count)
                best = new M3uImport(ReadTitle(text) ?? Path.GetFileNameWithoutExtension(path), tracks, missing, elsewhere);
        }
        return best!;
    }

    /// <summary>
    /// Path lagu di teks M3U, berurutan (lagu yang muncul dua kali tetap dua kali), sebagai path lengkap. Path relatif
    /// dihitung dari <paramref name="playlistFolder"/>, garis miring "/" juga diterima, dan URL file:/// diubah menjadi path.
    /// Baris kosong, komentar, dan alamat internet (radio) dilewati.
    /// </summary>
    public static IReadOnlyList<string> ParsePaths(string text, string playlistFolder)
    {
        var paths = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            string? path = line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? FromFileUrl(line)
                : UrlScheme().IsMatch(line) ? null
                : line.Replace('/', '\\');
            if (path is null)
                continue;

            try
            {
                paths.Add(Path.GetFullPath(Path.Combine(playlistFolder, path)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Baris yang bukan path (karakter yang tidak boleh ada di nama file): dilewati
            }
        }
        return paths;
    }

    private static string? FromFileUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;

    /// <summary>Nama playlist dari baris #PLAYLIST: (ditulis sebagian aplikasi, termasuk aplikasi ini), atau null.</summary>
    public static string? ReadTitle(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().Trim('﻿');
            if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase) && line[10..].Trim() is { Length: > 0 } title)
                return title;
        }
        return null;
    }

    /// <summary>
    /// Mencocokkan path dengan lagu library, berurutan. Path yang sama persis (tanpa membedakan huruf besar kecil) langsung
    /// cocok. Kalau tidak ada, misalnya playlist dari komputer lain atau sebelum musiknya dipindah, dicari lagu dengan nama
    /// file yang sama yang juga berada di folder dengan nama yang sama ("Album\01 lagu.flac"): paling sedikit nama file
    /// dan satu folder di atasnya harus sama, dan kalau beberapa lagu sama cocoknya, path itu dianggap tidak ditemukan.
    /// </summary>
    public static (IReadOnlyList<Track> Tracks, IReadOnlyList<string> Missing, int FoundElsewhere) Match(
        IReadOnlyList<string> paths, IReadOnlyList<Track> library)
    {
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in library)
        {
            byPath.TryAdd(track.FilePath, track);
            string name = Path.GetFileName(track.FilePath);
            if (!byName.TryGetValue(name, out var sameName))
                byName[name] = sameName = [];
            sameName.Add(track);
        }

        var tracks = new List<Track>();
        var missing = new List<string>();
        int elsewhere = 0;
        foreach (string path in paths)
        {
            if (byPath.TryGetValue(path, out var exact))
            {
                tracks.Add(exact);
            }
            else if (byName.TryGetValue(Path.GetFileName(path), out var candidates) && BestBySuffix(path, candidates) is { } moved)
            {
                tracks.Add(moved);
                elsewhere++;
            }
            else
            {
                missing.Add(path);
            }
        }
        return (tracks, missing, elsewhere);
    }

    // Lagu yang bagian akhir path-nya paling banyak sama (nama file, lalu folder-folder di atasnya); minimal dua bagian
    private static Track? BestBySuffix(string path, List<Track> candidates)
    {
        string[] wanted = Segments(path);
        Track? best = null;
        int bestCount = 1;
        bool tie = false;
        foreach (var candidate in candidates)
        {
            int count = CommonSuffix(wanted, Segments(candidate.FilePath));
            if (count > bestCount)
            {
                (best, bestCount, tie) = (candidate, count, false);
            }
            else if (count == bestCount && best is not null)
            {
                tie = true;
            }
        }
        return tie ? null : best;
    }

    private static string[] Segments(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static int CommonSuffix(string[] a, string[] b)
    {
        int count = 0;
        while (count < a.Length && count < b.Length
               && string.Equals(a[^(count + 1)], b[^(count + 1)], StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    /// <summary>
    /// Teks M3U8 (disimpan sebagai UTF-8) untuk lagu-lagu ini, dengan nama playlist, durasi dan "Artis - Judul" setiap lagu
    /// (#EXTINF), dan path lengkapnya. Baris diakhiri CRLF, seperti file teks Windows pada umumnya.
    /// </summary>
    public static string Write(IEnumerable<Track> tracks, string? title = null) =>
        WriteText(tracks.Select(t => (t, t.FilePath)), title);

    /// <summary>
    /// Seperti <see cref="Write"/>, untuk file yang disimpan di <paramref name="folder"/>: path lagu ditulis relatif terhadap
    /// folder itu kalau bisa, dengan garis miring "/", supaya playlist tetap jalan setelah folder musiknya disalin ke tempat
    /// lain, misalnya ke kartu SD pemutar portabel. Lagu di dalam folder itu ditulis relatif langsung. Lagu lain dicari di
    /// folder itu dengan bagian akhir path-nya, yang terpanjang lebih dulu: playlist yang disimpan di root kartu SD berisi
    /// salinan folder "Japan" menulis "Japan/Yorushika/創作/02 春泥棒.flac". Lagu yang tidak ditemukan ditulis dengan path
    /// lengkapnya.
    /// </summary>
    /// <param name="fileExists">Untuk tes; bawaannya File.Exists.</param>
    public static M3uExport Export(IReadOnlyList<Track> tracks, string? title, string folder, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string root = LibraryPaths.NormalizeFolder(folder);
        int relative = 0;
        var lines = new List<(Track, string)>(tracks.Count);
        foreach (var track in tracks)
        {
            string? path = RelativePath(track.FilePath, root, fileExists);
            if (path is not null)
                relative++;
            lines.Add((track, path ?? track.FilePath));
        }
        return new M3uExport(WriteText(lines, title), relative, tracks.Count - relative);
    }

    private static string? RelativePath(string path, string root, Func<string, bool> fileExists)
    {
        if (LibraryPaths.IsUnder(path, root) && path.Length > root.Length)
            return path[root.Length..].Replace('\\', '/');

        // Bagian akhir path, dari yang terpanjang (tanpa huruf drive) sampai folder terakhir dan nama file
        string[] segments = Segments(path);
        for (int start = 1; start <= segments.Length - 2; start++)
        {
            string suffix = string.Join('\\', segments[start..]);
            if (fileExists(root + suffix))
                return suffix.Replace('\\', '/');
        }
        return null;
    }

    private static string WriteText(IEnumerable<(Track Track, string Path)> lines, string? title)
    {
        var text = new StringBuilder("#EXTM3U\r\n");
        if (!string.IsNullOrWhiteSpace(title))
            text.Append("#PLAYLIST:").Append(OneLine(title)).Append("\r\n");

        foreach (var (track, path) in lines)
        {
            text.Append("#EXTINF:").Append((int)Math.Round(track.Duration.TotalSeconds)).Append(',')
                .Append(OneLine($"{track.DisplayArtist} - {track.DisplayTitle}")).Append("\r\n")
                .Append(path).Append("\r\n");
        }
        return text.ToString();
    }

    // Tag dengan baris baru akan memecah baris #EXTINF dan membuat sisa tag-nya terbaca sebagai path
    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}

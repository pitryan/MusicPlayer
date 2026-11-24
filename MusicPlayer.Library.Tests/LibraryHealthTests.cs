using System.Text;
using MusicPlayer.Library;
using TagLib;
using File = System.IO.File;

namespace MusicPlayer.Library.Tests;

public sealed class LibraryHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.LibraryHealthTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK1sAAAAASUVORK5CYII=");

    public LibraryHealthTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Song(string relative, bool cover = false, int paddingKb = 0)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Tags", "silence.flac"), path);
        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Keep title" },
            cover ? [new TagPicture(PictureType.FrontCover, "image/png", Png)] : null);
        if (paddingKb > 0)
            AppendMetadata(path, 1, new byte[paddingKb * 1024]);
        return path;
    }

    private string Dummy(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Scan_ListsOnlyLargePadding()
    {
        string large = Song(@"Album\01 Large.flac", paddingKb: 96);
        Song(@"Album\02 Small.flac", paddingKb: 8);

        var item = Assert.Single(LibraryHealth.Scan([_root]).Padding);
        Assert.Equal(large, item.Path);
        Assert.True(item.Bytes >= 96 * 1024);
    }

    [Fact]
    public void RemovePadding_KeepsTagsCoverAndAudio()
    {
        string path = Song(@"Album\01 Song.flac", cover: true, paddingKb: 96);
        byte[] before = File.ReadAllBytes(path);
        long padding = Assert.Single(LibraryHealth.Scan([_root]).Padding).Bytes;

        LibraryHealth.RemovePadding(path);

        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(before.Length - padding, after.Length);
        Assert.DoesNotContain(Blocks(after), b => b.Type == 1);
        Assert.Equal(before[AudioOffset(before)..], after[AudioOffset(after)..]);
        var tags = TagEditor.Read(path);
        Assert.Equal("Keep title", tags.Get("title"));
        Assert.Equal(Png, Assert.Single(tags.Pictures).Data);
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Empty(LibraryHealth.Scan([_root]).Padding);
    }

    [Fact]
    public void RemovePadding_KeepsId3Prefix()
    {
        string path = Song(@"Album\01 Song.flac", paddingKb: 96);
        byte[] id3 = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, 10, .. new byte[10]];
        File.WriteAllBytes(path, [.. id3, .. File.ReadAllBytes(path)]);

        LibraryHealth.RemovePadding(path);

        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(id3, after[..id3.Length]);
        Assert.Equal("fLaC"u8.ToArray(), after[id3.Length..(id3.Length + 4)]);
        Assert.Empty(LibraryHealth.Scan([_root]).Padding);
    }

    [Fact]
    public void RemovePadding_ReadOnlyFile_IsLeftAlone()
    {
        string path = Song(@"Album\01 Song.flac", paddingKb: 96);
        byte[] before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => LibraryHealth.RemovePadding(path));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Scan_ListsFolderImagesOnlyWhenEverySongHasEmbeddedCover()
    {
        Song(@"Embedded\01 A.flac", cover: true);
        Song(@"Embedded\02 B.flac", cover: true);
        string spare = Dummy(@"Embedded\cover.jpg");
        string scan = Dummy(@"Embedded\booklet.png");
        Song(@"Mixed\01 A.flac", cover: true);
        Song(@"Mixed\02 B.flac");
        Dummy(@"Mixed\cover.jpg");
        Song(@"None\01 A.flac");
        Dummy(@"None\folder.jpg");
        Dummy(@"NoSongs\cover.jpg");

        var images = LibraryHealth.Scan([_root]).SpareImages.Select(i => i.Path).ToList();

        Assert.Equal(new[] { scan, spare }.Order(StringComparer.CurrentCultureIgnoreCase), images);
    }

    [Fact]
    public void Scan_MatchesUnmatchedLyricsByTrackNumber()
    {
        Dummy(@"Single\01 カナデトモスソラ.flac");
        Dummy(@"Single\02 再生.flac");
        Dummy(@"Single\04 Instrumental.flac");
        string renamed = Dummy(@"Single\01 Kanadetomosusora.lrc");
        Dummy(@"Single\02 再生.lrc");
        string orphan = Dummy(@"Single\03 Other Song.lrc");

        var lyrics = LibraryHealth.Scan([_root]).LyricFiles;

        Assert.Equal(2, lyrics.Count);
        Assert.Contains(new LyricFileItem(renamed, Path.Combine(_root, "Single", "01 カナデトモスソラ.lrc")), lyrics);
        Assert.Contains(new LyricFileItem(orphan, null), lyrics);
    }

    [Fact]
    public void Scan_LeavesUncertainLyricsOut()
    {
        // Nama dari Mac: "ド" sebagai ト + tanda dakuten tetap dianggap sama (seperti LyricsFinder)
        Dummy(@"Mac\05 ドラマ.flac");
        Dummy(@"Mac\05 ドラマ.lrc".Normalize(NormalizationForm.FormD));
        // Dua .lrc berebut satu lagu
        Dummy(@"Rivals\06 Song.flac");
        Dummy(@"Rivals\06 Romaji.lrc");
        Dummy(@"Rivals\06 English.lrc");
        // Lagu tanpa nomor: tidak bisa dipastikan .lrc ini milik lagu lain
        Dummy(@"Unnumbered\Song.flac");
        Dummy(@"Unnumbered\01 Other.lrc");
        // .lrc tanpa nomor
        Dummy(@"NoNumber\01 Song.flac");
        Dummy(@"NoNumber\Lyrics.lrc");

        Assert.Empty(LibraryHealth.Scan([_root]).LyricFiles);
    }

    [Fact]
    public void FixLyricFile_RenamesOrDiscards()
    {
        string lrc = Dummy(@"Album\01 Romaji.lrc", "lyrics");
        string target = Path.Combine(_root, "Album", "01 Song.lrc");
        string orphan = Dummy(@"Album\03 Gone.lrc");
        var discarded = new List<string>();

        LibraryHealth.FixLyricFile(new LyricFileItem(lrc, target), discarded.Add);
        LibraryHealth.FixLyricFile(new LyricFileItem(orphan, null), discarded.Add);

        Assert.False(File.Exists(lrc));
        Assert.Equal("lyrics", File.ReadAllText(target));
        Assert.Equal(new[] { orphan }, discarded);
    }

    [Fact]
    public void FixLyricFile_DoesNotOverwrite()
    {
        string lrc = Dummy(@"Album\01 Romaji.lrc", "new");
        string target = Dummy(@"Album\01 Song.lrc", "existing");

        Assert.Throws<IOException>(() => LibraryHealth.FixLyricFile(new LyricFileItem(lrc, target), _ => { }));
        Assert.Equal("existing", File.ReadAllText(target));
        Assert.True(File.Exists(lrc));
    }

    [Fact]
    public void Scan_ListsBackupsAndOldTemporaryCopies()
    {
        string backup = Dummy(@"Album\01 Song.lrc.bak");
        string old = Dummy($@"Album\.{Guid.NewGuid():N}.tmp");
        File.SetAttributes(old, FileAttributes.Hidden);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        string fresh = Dummy($@"Album\.{Guid.NewGuid():N}.tmp");
        File.SetAttributes(fresh, FileAttributes.Hidden);
        Dummy(@"Album\other.tmp");

        var leftovers = LibraryHealth.Scan([_root]).Leftovers.Select(l => l.Path).ToList();

        Assert.Equal(2, leftovers.Count);
        Assert.Contains(backup, leftovers);
        Assert.Contains(old, leftovers);
    }

    [Fact]
    public void Scan_NestedLibraryFolders_ListEachFileOnce()
    {
        string backup = Dummy(@"Japan\Album\01 Song.lrc.bak");

        var leftovers = LibraryHealth.Scan([_root, Path.Combine(_root, "Japan") + Path.DirectorySeparatorChar]).Leftovers;

        Assert.Equal(backup, Assert.Single(leftovers).Path);
    }

    private static void AppendMetadata(string path, byte type, byte[] payload)
    {
        byte[] old = File.ReadAllBytes(path);
        int audio = AudioOffset(old), last = 4;
        while (last + 4 + BlockLength(old, last) < audio) last += 4 + BlockLength(old, last);
        old[last] &= 0x7F;
        using var output = File.Create(path);
        output.Write(old.AsSpan(0, audio));
        output.Write(new byte[] { (byte)(type | 128), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length });
        output.Write(payload);
        output.Write(old.AsSpan(audio));
    }

    private static IEnumerable<(int Type, int Length)> Blocks(byte[] bytes)
    {
        int at = 4;
        while (true)
        {
            int size = BlockLength(bytes, at);
            yield return (bytes[at] & 127, size);
            if ((bytes[at] & 128) != 0) yield break;
            at += 4 + size;
        }
    }

    private static int BlockLength(byte[] data, int at) => (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

    private static int AudioOffset(byte[] data)
    {
        int at = 4;
        while (true)
        {
            int next = at + 4 + BlockLength(data, at);
            if ((data[at] & 128) != 0) return next;
            at = next;
        }
    }
}

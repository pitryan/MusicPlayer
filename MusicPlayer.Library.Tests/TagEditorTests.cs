using static MusicPlayer.Library.Tests.TestLyrics;

namespace MusicPlayer.Library.Tests;

/// <summary>
/// Tes menulis tag ke file sungguhan. Setiap tes memakai salinan file dari TestData/Tags di folder sementaranya
/// sendiri, jadi file aslinya tidak pernah berubah dan tes bisa berjalan bersamaan.
/// </summary>
public sealed class TagEditorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "MusicPlayer.Library.Tests", Guid.NewGuid().ToString("N"));

    public TagEditorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        // Atribut read-only dibuang dulu, karena file read-only tidak bisa dihapus
        foreach (string file in Directory.EnumerateFiles(_folder))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_folder, recursive: true);
    }

    private string Copy(string fixture)
    {
        string path = Path.Combine(_folder, fixture);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Tags", fixture), path);
        return path;
    }

    // ── Cara baca dan terjemahan lirik ──────────────────────

    [Theory]
    [InlineData("silence.flac")]
    [InlineData("silence.mp3")]
    [InlineData("silence.m4a")]
    public void Write_LyricLayers_StayOnTheirRowsWhenFirstRowsAreEmpty(string fixture)
    {
        string path = Copy(fixture);
        // Seperti yang disimpan editor lirik: jeda instrumental di awal tanpa lapisan, lalu baris yang hanya punya
        // terjemahan, jadi kedua tag diawali baris kosong
        var lyrics = Synced((0, ""), (5, "Light, light"), (10, "もうちょっとだから"), (15, "夜明けまで"));
        string?[] readings = [null, null, "Mou chotto dakara", "Yoake made"];
        string?[] translations = [null, "Cahaya, cahaya", null, null];
        string reading = LyricLayers.JoinLines(readings), translation = LyricLayers.JoinLines(translations);

        TagEditor.Write(path, new Dictionary<string, string>
        {
            ["lyrics"] = LrcWriter.Format(lyrics),
            ["lyrics_reading"] = reading,
            ["lyrics_translation"] = translation,
        });

        Assert.Equal(new[] { path }, Directory.GetFiles(_folder)); // salinan sementara sudah menggantikan file asli
        var tags = TagEditor.Read(path);
        Assert.Equal(reading, tags.Get("lyrics_reading"));
        Assert.Equal(translation, tags.Get("lyrics_translation"));

        var found = new LyricsFinder(null, null).FindLocal(new Track { FilePath = path, FolderPath = _folder });
        Assert.NotNull(found);
        Assert.Equal(Texts(lyrics), Texts(found.Lyrics));
        Assert.Equal(readings, Readings(found.Lyrics));
        Assert.Equal(translations, Translations(found.Lyrics));
    }

    [Theory]
    [InlineData("silence.flac")]
    [InlineData("silence.mp3")]
    [InlineData("silence.m4a")]
    public void Write_BlankLyricLayer_RemovesTag(string fixture)
    {
        string path = Copy(fixture);
        TagEditor.Write(path, new Dictionary<string, string> { ["lyrics_reading"] = "\nichi" });

        TagEditor.Write(path, new Dictionary<string, string> { ["lyrics_reading"] = " \n \n" });

        Assert.False(TagEditor.Read(path).Fields.ContainsKey("lyrics_reading"));
    }

    [Fact]
    public void Write_TrimsValues_ButKeepsLeadingLinesOfLyricLayers()
    {
        string path = Copy("silence.flac");

        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "\n  Title  \n", ["lyrics_translation"] = "\n satu \n\n" });

        var tags = TagEditor.Read(path);
        Assert.Equal("Title", tags.Get("title"));
        Assert.Equal("\n satu", tags.Get("lyrics_translation"));
    }

    // ── File read-only ──────────────────────────────────────

    [Fact]
    public void Write_ReadOnlyFile_FailsNamingTheSongAndLeavesNothingBehind()
    {
        string path = Copy("silence.flac");
        byte[] before = File.ReadAllBytes(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var error = Assert.Throws<UnauthorizedAccessException>(
            () => TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Title" }));

        Assert.Contains("silence.flac", error.Message);
        Assert.Contains("read-only", error.Message);
        Assert.DoesNotContain(".tmp", error.Message);
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder)); // tidak ada salinan sementara yang tertinggal
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ── Salinan sementara ───────────────────────────────────
    // File.Replace yang gagal setelah menghapus file asli (ERROR_UNABLE_TO_MOVE_REPLACEMENT) hanya terjadi kalau ada
    // proses lain (antivirus) yang membuka salinannya tepat di tengah penggantian, jadi keputusan pembersihannya dites
    // langsung.

    [Fact]
    public void CleanUpTemporary_OriginalGone_PutsFinishedCopyBack()
    {
        string path = Path.Combine(_folder, "song.flac"), temporary = Path.Combine(_folder, ".song.flac.tmp");
        File.WriteAllText(temporary, "new tags");

        TagEditor.CleanUpTemporary(temporary, path, complete: true);

        Assert.Equal("new tags", File.ReadAllText(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void CleanUpTemporary_OriginalThere_DeletesCopyEvenIfReadOnly()
    {
        string path = Path.Combine(_folder, "song.flac"), temporary = Path.Combine(_folder, ".song.flac.tmp");
        File.WriteAllText(path, "original");
        File.WriteAllText(temporary, "new tags");
        File.SetAttributes(temporary, FileAttributes.ReadOnly | FileAttributes.Hidden);

        TagEditor.CleanUpTemporary(temporary, path, complete: true);

        Assert.Equal("original", File.ReadAllText(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void CleanUpTemporary_UnfinishedCopy_IsNeverPutBack()
    {
        string path = Path.Combine(_folder, "song.flac"), temporary = Path.Combine(_folder, ".song.flac.tmp");
        File.WriteAllText(temporary, "half written");

        TagEditor.CleanUpTemporary(temporary, path, complete: false);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void CleanUpTemporary_CopyInUse_DoesNotThrow()
    {
        string path = Path.Combine(_folder, "song.flac"), temporary = Path.Combine(_folder, ".song.flac.tmp");
        File.WriteAllText(path, "original");
        File.WriteAllText(temporary, "new tags");

        using (new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None))
            TagEditor.CleanUpTemporary(temporary, path, complete: true); // error penyimpanan aslinya tidak tertutup

        Assert.Equal("original", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_SongDeletedOrMovedWhileCopyIsWritten_IsNotPutBack(bool moved)
    {
        string path = Copy("silence.flac"), movedPath = Path.Combine(_folder, "moved.flac");
        // Aplikasi lain menghapus atau memindahkan lagunya saat salinan sementara sedang ditulis. Daftar gambar dibaca
        // di tengah penulisan salinan, jadi aksinya selalu terjadi di titik yang sama.
        var pictures = new OnRead(() =>
        {
            if (moved)
                File.Move(path, movedPath);
            else
                File.Delete(path);
        });

        Assert.ThrowsAny<IOException>(() => TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Title" }, pictures));

        // Path lama tetap kosong (lagu tidak muncul lagi atau jadi ganda), dan salinan sementara tidak tertinggal
        Assert.Equal(moved ? [movedPath] : Array.Empty<string>(), Directory.GetFiles(_folder));
    }

    /// <summary>Daftar gambar kosong yang menjalankan sebuah aksi saat isinya dibaca.</summary>
    private sealed class OnRead(Action action) : IReadOnlyList<TagPicture>
    {
        public int Count => 0;

        public TagPicture this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<TagPicture> GetEnumerator()
        {
            action();
            yield break;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

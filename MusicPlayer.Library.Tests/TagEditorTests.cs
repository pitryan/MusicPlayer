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

    private string Copy(string fixture, string? name = null)
    {
        string path = Path.Combine(_folder, name ?? fixture);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Tags", fixture), path);
        return path;
    }

    /// <summary>Isi file seperti yang dibaca library saat scan.</summary>
    private static Track Library(string path) => TagReader.Read(path, FileStamp.From(new FileInfo(path)), out _);

    /// <summary>Mengubah tag langsung lewat TagLib, seperti aplikasi lain yang menandai file ini sebelumnya.</summary>
    private static void EditWithTagLib(string path, Action<TagLib.File> edit)
    {
        using var file = TagLib.File.Create(path);
        edit(file);
        file.Save();
    }

    private static List<T> Id3Frames<T>(string path) where T : TagLib.Id3v2.Frame
    {
        using var file = TagLib.File.Create(path);
        return ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2)).GetFrames<T>().ToList();
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

    // ── Tag lama (ID3v1, APE, RIFF INFO) ────────────────────
    // Library membaca tag gabungan TagLib: field yang kosong di tag utama diambil dari tag lain di file. legacy.mp3
    // berisi ID3v2.3 (judul, album "Old Album", genre Blues, tahun 1999), APE (lirik, disc 2, REPLAYGAIN_TRACK_GAIN
    // milik mp3gain), dan ID3v1 (artis "Old Artist", album, tahun, genre, track 7). legacy.wav hanya berisi RIFF INFO
    // buatan ffmpeg.

    [Fact]
    public void Write_ClearedFields_StayClearedInLibraryWhenMp3HasLegacyTags()
    {
        string path = Copy("legacy.mp3");

        TagEditor.Write(path, new Dictionary<string, string> { ["genre"] = "", ["album"] = "", ["date"] = "" });

        var tags = TagEditor.Read(path);
        Assert.Equal("", tags.Get("genre") + tags.Get("album") + tags.Get("date"));
        var track = Library(path);
        Assert.Null(track.Genre);
        Assert.Null(track.Album);
        Assert.Null(track.Year);
        Assert.Equal("Song", track.Title);
    }

    [Fact]
    public void Read_ShowsValuesThatOnlyLegacyTagsHave()
    {
        string path = Copy("legacy.mp3");

        var tags = TagEditor.Read(path);

        Assert.Equal("Old Artist", tags.Get("artist")); // ID3v1
        Assert.Equal(Library(path).Artist, tags.Get("artist"));
        Assert.Equal("7", tags.Get("tracknumber")); // ID3v1
        Assert.Equal("2", tags.Get("discnumber")); // APE
        Assert.Equal("Old APE lyrics", tags.Get("lyrics")); // APE
        Assert.Equal(TagReader.ReadLyrics(path), tags.Get("lyrics"));
    }

    [Fact]
    public void Write_LegacyOnlyValues_AreChangedAndClearedEverywhere()
    {
        string path = Copy("legacy.mp3");

        TagEditor.Write(path, new Dictionary<string, string> { ["artist"] = "New Artist", ["discnumber"] = "", ["lyrics"] = "" });

        var track = Library(path);
        Assert.Equal("New Artist", track.Artist);
        Assert.Null(track.DiscNumber);
        Assert.False(track.HasLyrics);
        Assert.Null(TagReader.ReadLyrics(path));
        Assert.Equal(7, track.TrackNumber); // field yang tidak diubah tetap seperti sebelumnya
        Assert.Equal("7", TagEditor.Read(path).Get("tracknumber"));

        // Tag lama tetap ada dan ikut diperbarui (untuk pemutar yang hanya membaca ID3v1), dan data aplikasi lain di
        // APE tidak hilang
        using var file = TagLib.File.Create(path);
        Assert.Equal("New Artist", file.GetTag(TagLib.TagTypes.Id3v1).JoinedPerformers);
        Assert.Equal("-6.00 dB", ((TagLib.Ape.Tag)file.GetTag(TagLib.TagTypes.Ape)).GetItem("REPLAYGAIN_TRACK_GAIN")?.ToString());
    }

    [Fact]
    public void Write_Wav_RiffInfoIsShownAndKeptInStepWithEditor()
    {
        string path = Copy("legacy.wav");
        var before = TagEditor.Read(path);
        Assert.Equal("Info Title", before.Get("title"));
        Assert.Equal("Info Genre", before.Get("genre"));

        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "New Title", ["genre"] = "" });

        var track = Library(path);
        Assert.Equal("New Title", track.Title);
        Assert.Null(track.Genre);
        Assert.Equal(1999, track.Year);
        Assert.Equal("1999", TagEditor.Read(path).Get("date")); // tidak diubah, tetap dari INFO
        using (var file = TagLib.File.Create(path))
        {
            Assert.Equal("New Title", file.GetTag(TagLib.TagTypes.RiffInfo).Title); // untuk aplikasi yang hanya membaca INFO
            Assert.Equal(TagLib.TagTypes.Id3v2 | TagLib.TagTypes.RiffInfo, file.TagTypesOnDisk); // tanpa chunk "MID "/DivX baru
        }

        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "" });

        Assert.Null(Library(path).Title);
        Assert.Equal("", TagEditor.Read(path).Get("title"));
    }

    [Fact]
    public void Write_Flac_ValueOnlyInId3TagIsShownAndCleared()
    {
        string path = Copy("silence.flac");
        EditWithTagLib(path, file => file.GetTag(TagLib.TagTypes.Id3v1, true).Album = "Old Album");
        Assert.Equal("Old Album", Library(path).Album);
        Assert.Equal("Old Album", TagEditor.Read(path).Get("album"));

        TagEditor.Write(path, new Dictionary<string, string> { ["album"] = "" });

        Assert.Null(Library(path).Album);
        Assert.Equal("", TagEditor.Read(path).Get("album"));
    }

    [Fact]
    public void Write_Mp3WithOnlyId3v1_IsShownLikeLibraryAndKeepsId3v1InStep()
    {
        // File lama yang hanya punya ID3v1; ID3v1 menyimpan genre sebagai nomor (17 = Rock)
        string path = Copy("silence.mp3");
        EditWithTagLib(path, file =>
        {
            file.RemoveTags(TagLib.TagTypes.Id3v2);
            var id3v1 = file.GetTag(TagLib.TagTypes.Id3v1, true);
            id3v1.Title = "Old Title";
            id3v1.Album = "Old Album";
            id3v1.Genres = ["Rock"];
        });
        var tags = TagEditor.Read(path);
        Assert.Equal("Old Title", tags.Get("title"));
        Assert.Equal("Rock", tags.Get("genre"));

        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "New Title", ["album"] = "" });

        var track = Library(path);
        Assert.Equal("New Title", track.Title);
        Assert.Null(track.Album);
        Assert.Equal("Rock", track.Genre);
        using var file = TagLib.File.Create(path);
        Assert.Equal("New Title", file.GetTag(TagLib.TagTypes.Id3v1).Title);
    }

    [Fact]
    public void Write_Mp3WithOnlyId3v2_DoesNotGetAStaleId3v1Copy()
    {
        // TagLib membuat ID3v1 di memori berisi salinan nilai ID3v2 saat membuka MP3 (sebelum diubah), lalu ikut
        // menyimpannya
        string path = Copy("silence.mp3");
        EditWithTagLib(path, file =>
        {
            file.GetTag(TagLib.TagTypes.Id3v2, true).Album = "Old Album";
            file.RemoveTags(TagLib.TagTypes.Id3v1);
        });

        TagEditor.Write(path, new Dictionary<string, string> { ["album"] = "" });

        Assert.Null(Library(path).Album);
        using var file = TagLib.File.Create(path);
        Assert.Equal(TagLib.TagTypes.Id3v2, file.TagTypesOnDisk);
    }

    // ── AAC mentah (ADTS) ───────────────────────────────────

    [Fact]
    public void Write_RawAacFile_IsTaggedLikeMp3()
    {
        string path = Copy("silence.aac");
        Assert.Equal(TagFormat.Id3, TagEditor.Read(path).Format);

        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Title", ["lyrics"] = "Lyrics" });

        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
        var tags = TagEditor.Read(path);
        Assert.Equal("Title", tags.Get("title"));
        Assert.Equal("Lyrics", tags.Get("lyrics"));
        Assert.Equal("Title", Library(path).Title);
    }

    [Theory]
    [InlineData("song.aac")]
    [InlineData("song.mp3")]
    public void Write_Mp4DataUnderAnotherExtension_IsRefusedAndLeftUntouched(string name)
    {
        // Editor dan library membaca file sesuai ekstensinya: tag MP4 yang ditulis ke sini tidak pernah terbaca, dan
        // tag ID3 di depan box "ftyp" membuat file tidak bisa diputar
        string path = Copy("silence.m4a", name);
        byte[] before = File.ReadAllBytes(path);

        var error = Assert.Throws<InvalidDataException>(() =>
            TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Title" }));

        Assert.Contains("Rename it to .m4a", error.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    // ── Jumlah track/disc dan jenis rilis di tag lama ───────

    [Fact]
    public void Write_TotalsOnly_KeepTheNumberShownFromLegacyTags()
    {
        // legacy.mp3: nomor track hanya ada di ID3v1 (7), nomor disc hanya di APE (2); editor menampilkan keduanya
        string path = Copy("legacy.mp3");

        TagEditor.Write(path, new Dictionary<string, string> { ["totaltracks"] = "12", ["totaldiscs"] = "3" });

        var tags = TagEditor.Read(path);
        Assert.Equal(("7", "12", "2", "3"), (tags.Get("tracknumber"), tags.Get("totaltracks"), tags.Get("discnumber"), tags.Get("totaldiscs")));
        var track = Library(path);
        Assert.Equal(((int?)7, (int?)12, (int?)2, (int?)3), (track.TrackNumber, track.TrackCount, track.DiscNumber, track.DiscCount));
        using var file = TagLib.File.Create(path);
        var ape = (TagLib.Ape.Tag)file.GetTag(TagLib.TagTypes.Ape);
        Assert.Equal("7/12", ape.GetItem("Track")?.ToString());
        Assert.Equal("2/3", ape.GetItem("Disc")?.ToString());
    }

    [Fact]
    public void Write_FlacReleaseType_IsCopiedToTheEmbeddedId3Tag()
    {
        // TagLib membaca jenis rilis Vorbis dari MUSICBRAINZ_ALBUMTYPE (di sini nilai lama "ep"), editor menulis RELEASETYPE
        string path = Copy("silence.flac");
        EditWithTagLib(path, file =>
        {
            file.GetTag(TagLib.TagTypes.Id3v2, true).MusicBrainzReleaseType = "album";
            ((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true)).SetField("MUSICBRAINZ_ALBUMTYPE", "ep");
        });

        TagEditor.Write(path, new Dictionary<string, string> { ["releasetype"] = "single" });
        Assert.Equal("single", Id3ReleaseType(path));

        TagEditor.Write(path, new Dictionary<string, string> { ["releasetype"] = "album; compilation" });
        Assert.Equal("album;compilation", Id3ReleaseType(path));

        static string? Id3ReleaseType(string path)
        {
            using var file = TagLib.File.Create(path);
            return file.GetTag(TagLib.TagTypes.Id3v2).MusicBrainzReleaseType;
        }
    }

    // ── Nama file panjang ───────────────────────────────────

    [Fact]
    public void Write_FileNameOfMaximumLength_Works()
    {
        // NTFS mengizinkan nama file sampai 255 karakter; salinan sementaranya juga harus muat di folder yang sama
        string path = Copy("silence.mp3", new string('a', 251) + ".mp3");

        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Title" });

        Assert.Equal("Title", TagEditor.Read(path).Get("title"));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    // ── Lirik dan komentar MP3 ──────────────────────────────
    // lyrics-jpn.mp3 berisi lirik (USLT) dan komentar (COMM) berbahasa "jpn" seperti hasil rip lagu Jepang, plus
    // komentar iTunNORM milik iTunes. Properti Lyrics dan Comment milik TagLib hanya mengganti frame berbahasa sistem.

    [Fact]
    public void Write_Lyrics_ReplacesFrameInAnotherLanguage()
    {
        string path = Copy("lyrics-jpn.mp3");
        Assert.Equal("古い歌詞", TagEditor.Read(path).Get("lyrics"));

        TagEditor.Write(path, new Dictionary<string, string> { ["lyrics"] = "新しい歌詞" });

        // Pemutar lain umumnya membaca frame pertama, jadi lirik lama tidak boleh tertinggal
        var frame = Assert.Single(Id3Frames<TagLib.Id3v2.UnsynchronisedLyricsFrame>(path));
        Assert.Equal("新しい歌詞", frame.Text);
        Assert.Equal("jpn", frame.Language);
        Assert.Equal("新しい歌詞", TagReader.ReadLyrics(path));
    }

    [Fact]
    public void Write_Comment_ReplacesFrameInAnotherLanguageAndKeepsITunesData()
    {
        string path = Copy("lyrics-jpn.mp3");

        TagEditor.Write(path, new Dictionary<string, string> { ["comment"] = "New comment" });

        var frames = Id3Frames<TagLib.Id3v2.CommentsFrame>(path);
        Assert.Equal("New comment", Assert.Single(frames, f => f.Description == "").Text);
        Assert.Contains(frames, f => f.Description == "iTunNORM");
        Assert.Equal("New comment", TagEditor.Read(path).Get("comment"));
    }

    [Fact]
    public void Write_Lyrics_ReplacesLyricsInTxxxFrame()
    {
        // foobar2000 dan plugin liriknya menyimpan lirik MP3 di TXXX:LYRICS, yang juga dibaca library
        string path = Copy("silence.mp3");
        EditWithTagLib(path, file => TagLib.Id3v2.UserTextInformationFrame.Get(
            (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true), "LYRICS", true).Text = ["Old lyrics"]);
        Assert.Equal("Old lyrics", TagEditor.Read(path).Get("lyrics"));

        TagEditor.Write(path, new Dictionary<string, string> { ["lyrics"] = "New lyrics" });

        Assert.Empty(Id3Frames<TagLib.Id3v2.UserTextInformationFrame>(path));
        Assert.Equal("New lyrics", TagReader.ReadLyrics(path));

        TagEditor.Write(path, new Dictionary<string, string> { ["lyrics"] = "" });

        Assert.Null(TagReader.ReadLyrics(path));
        Assert.Equal("", TagEditor.Read(path).Get("lyrics"));
    }

    [Fact]
    public void Write_LyricLayer_ReplacesFrameWrittenByAnotherApp()
    {
        // Lapisan lirik disimpan di TXXX yang dikenali dari deskripsinya saja (tanpa bahasa), jadi deskripsi berhuruf
        // kecil atau encoding lain dari aplikasi lain tetap diganti, bukan ditambah frame kedua
        string path = Copy("silence.mp3");
        EditWithTagLib(path, file => TagLib.Id3v2.UserTextInformationFrame.Get(
            (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true), "lyrics_reading", TagLib.StringType.Latin1, true, true).Text = ["old"]);

        TagEditor.Write(path, new Dictionary<string, string> { ["lyrics_reading"] = "\nnew" });

        Assert.Equal(["\nnew"], Assert.Single(Id3Frames<TagLib.Id3v2.UserTextInformationFrame>(path)).Text);
        Assert.Equal("\nnew", TagEditor.Read(path).Get("lyrics_reading"));
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

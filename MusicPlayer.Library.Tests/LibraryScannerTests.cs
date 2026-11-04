using Microsoft.Data.Sqlite;

namespace MusicPlayer.Library.Tests;

/// <summary>
/// Tes LibraryScanner dengan database dan folder musik sementara. Lagunya salinan dari TestData/Scanner (first.flac dan
/// second.flac: album dan artis sama, judul berbeda), jadi file asli tidak pernah tersentuh.
/// </summary>
public sealed class LibraryScannerTests : IDisposable
{
    private static readonly DateTime FirstModified = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondModified = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ThirdModified = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.Tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _database;
    private readonly LibraryScanner _scanner;

    public LibraryScannerTests()
    {
        _database = new LibraryDatabase(Path.Combine(_root, "library.db"));
        _scanner = new LibraryScanner(_database);
    }

    public void Dispose()
    {
        // Koneksi SQLite disimpan di pool dan tetap membuka file database; dilepas dulu supaya foldernya bisa dihapus
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Scan_FolderRemovedWhileScanning_DoesNotBringItsSongsBack()
    {
        string keptSong = CopyFixture("first.flac", "Kept", FirstModified);
        string removedSong = CopyFixture("first.flac", "Removed", FirstModified);
        string removedFolder = Path.GetDirectoryName(removedSong)!;
        _database.AddFolder(Path.GetDirectoryName(keptSong)!);
        _scanner.Scan();

        // Folder baru ditambahkan lalu scan dimulai, tapi folder itu dihapus lagi dari library setelah scan membaca
        // daftar folder dan tag-nya, tepat sebelum hasilnya disimpan
        _database.AddFolder(removedFolder);
        _scanner.Scan(new InlineProgress(p =>
        {
            if (p.Stage == ScanStage.Saving)
                _database.RemoveFolder(removedFolder);
        }));

        Assert.Equal(keptSong, Assert.Single(_database.GetTracks()).FilePath);
        Assert.Equal(0, _database.CountTracks(removedFolder));
    }

    [Fact]
    public void Scan_RemovesSongsLeftOutsideTheLibraryFolders()
    {
        string keptSong = CopyFixture("first.flac", "Kept", FirstModified);
        string orphanSong = CopyFixture("first.flac", "Orphan", FirstModified);
        _database.AddFolder(Path.GetDirectoryName(keptSong)!);
        _database.AddFolder(Path.GetDirectoryName(orphanSong)!);
        _scanner.Scan();

        // Database dari versi lama: foldernya sudah tidak ada di library, tapi lagunya tertinggal
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database.DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM library_folders WHERE path = $path";
            command.Parameters.AddWithValue("$path", LibraryPaths.NormalizeFolder(Path.GetDirectoryName(orphanSong)!));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Equal(2, _database.CountTracks());

        var result = _scanner.Scan();

        Assert.Equal(keptSong, Assert.Single(_database.GetTracks()).FilePath);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Scan_UnavailableFolder_KeepsItsSongs()
    {
        string song = CopyFixture("first.flac", "External", FirstModified);
        string folder = Path.GetDirectoryName(song)!;
        _database.AddFolder(folder);
        _scanner.Scan();

        // Seperti hard disk eksternal yang dicabut: foldernya masih di library, tapi tidak ada di disk
        Directory.Move(folder, folder + " (unplugged)");
        var result = _scanner.Scan();

        Assert.Single(result.UnavailableFolders);
        Assert.Equal(song, Assert.Single(_database.GetTracks()).FilePath);
    }

    [Fact]
    public void Scan_ChangedFileLockedByAnotherApp_KeepsOldTagsAndRereadsLater()
    {
        string song = CopyFixture("first.flac", "Music", FirstModified);
        _database.AddFolder(Path.GetDirectoryName(song)!);
        _scanner.Scan();

        // Tag file diubah aplikasi lain, yang masih memegang filenya tanpa berbagi selama scan ulang
        CopyFixture("second.flac", "Music", SecondModified);
        ScanResult lockedScan;
        using (Lock(song))
            lockedScan = _scanner.Scan();

        Assert.Single(lockedScan.Errors);
        var kept = Assert.Single(_database.GetTracks());
        Assert.Equal("First Title", kept.Title);
        Assert.Equal("Scanner Album", kept.Album);

        var unlockedScan = _scanner.Scan();

        Assert.Empty(unlockedScan.Errors);
        Assert.Equal("Second Title", Assert.Single(_database.GetTracks()).Title);
    }

    [Fact]
    public void Scan_NewFileLockedByAnotherApp_IsListedAndReadOnTheNextScan()
    {
        string folder = Path.Combine(_root, "Music");
        Directory.CreateDirectory(folder);
        _database.AddFolder(folder);
        string song = CopyFixture("first.flac", "Music", FirstModified);

        ScanResult lockedScan;
        using (Lock(song))
            lockedScan = _scanner.Scan();

        Assert.Single(lockedScan.Errors);
        var listed = Assert.Single(_database.GetTracks());
        Assert.Equal(song, listed.FilePath);
        Assert.Null(listed.Title);

        _scanner.Scan();

        Assert.Equal("First Title", Assert.Single(_database.GetTracks()).Title);
        // Tanggal ditambahkan tetap waktu ubah file, walaupun pembacaan pertamanya gagal
        Assert.Equal(FirstModified, Assert.Single(_database.GetAlbums()).AddedUtc);
    }

    [Fact]
    public void Scan_NewFileLockedThenChanged_KeepsTheDateItWasFound()
    {
        string folder = Path.Combine(_root, "Music");
        Directory.CreateDirectory(folder);
        _database.AddFolder(folder);
        string song = CopyFixture("first.flac", "Music", FirstModified);
        using (Lock(song))
            _scanner.Scan();

        // Tag-nya diubah (misalnya di aplikasi lain) sebelum sempat terbaca
        CopyFixture("second.flac", "Music", SecondModified);
        _scanner.Scan();

        Assert.Equal("Second Title", Assert.Single(_database.GetTracks()).Title);
        Assert.Equal(FirstModified, Assert.Single(_database.GetAlbums()).AddedUtc);
    }

    [Fact]
    public void Scan_CorruptFile_IsReportedAsErrorNotAsUpdated()
    {
        string song = CopyFixture("first.flac", "Music", FirstModified);
        _database.AddFolder(Path.GetDirectoryName(song)!);
        _scanner.Scan();

        // Rusak permanen: setiap scan gagal membacanya, jadi lagunya tetap dengan data lama
        File.WriteAllText(song, "not audio");
        File.SetLastWriteTimeUtc(song, SecondModified);

        for (int scan = 0; scan < 2; scan++)
        {
            var result = _scanner.Scan();
            Assert.Single(result.Errors);
            Assert.Equal(0, result.Updated);
        }
        Assert.Equal("First Title", Assert.Single(_database.GetTracks()).Title);
    }

    [Fact]
    public void ParentAddedAfterCaseOnlyRenameOfNonAsciiFolder_KeepsSongsAndTheirHistory()
    {
        // Folder album di dalam "élan" masuk library, lalu "élan" diganti namanya menjadi "Élan" (hanya huruf besar/kecil)
        // dan ditambahkan ke library. Path lagu di database masih "élan"; NOCASE di SQLite hanya melipat huruf ASCII.
        string song = CopyFixture("first.flac", Path.Combine("élan", "Album"), FirstModified);
        _database.AddFolder(Path.GetDirectoryName(song)!);
        _scanner.Scan();
        var before = Assert.Single(_database.GetTracks());
        _database.RecordPlay(song, SecondModified);
        _database.SetFavourite(song, true, SecondModified);
        long playlist = _database.CreatePlaylist("Mix", SecondModified);
        _database.AddToPlaylist(playlist, [song], SecondModified);

        string renamed = Path.Combine(_root, "Élan");
        Directory.Move(Path.Combine(_root, "élan"), renamed);
        Assert.Equal(AddFolderStatus.Added, _database.AddFolder(renamed).Status);

        _database.UpsertTracks([before]); // seperti menyimpan tag lagu itu
        Assert.Equal(before.Id, Assert.Single(_database.GetTracks()).Id);

        var result = _scanner.Scan();

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Added);
        Assert.Equal(before.Id, Assert.Single(_database.GetTracks()).Id);
        Assert.Single(_database.GetMostPlayed());
        Assert.Single(_database.GetFavouriteTracks());
        Assert.Single(_database.GetPlaylistEntries(playlist));
    }

    [Fact]
    public void Scan_ChangedSongInFolderRenamedByCaseOnly_UpdatesTheSameSong()
    {
        string song = CopyFixture("first.flac", Path.Combine("Music", "élan"), FirstModified);
        _database.AddFolder(Path.Combine(_root, "Music"));
        _scanner.Scan();
        long id = Assert.Single(_database.GetTracks()).Id;

        // Subfolder diganti namanya hanya huruf besar/kecil (non-ASCII), lalu tag lagunya diubah
        Directory.Move(Path.GetDirectoryName(song)!, Path.Combine(_root, "Music", "Élan"));
        CopyFixture("second.flac", Path.Combine("Music", "Élan"), SecondModified);
        var result = _scanner.Scan();

        var track = Assert.Single(_database.GetTracks());
        Assert.Equal(id, track.Id);
        Assert.Equal("Second Title", track.Title);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void Scan_LeftoverDuplicateRowFromCaseOnlyRename_IsStillUpdated()
    {
        string song = CopyFixture("first.flac", Path.Combine("Music", "élan"), FirstModified);
        _database.AddFolder(Path.Combine(_root, "Music"));
        _scanner.Scan();
        long id = Assert.Single(_database.GetTracks()).Id;

        // Scanner versi lama menyimpan lagu yang berubah setelah "élan" diganti menjadi "Élan" sebagai baris kedua
        string renamed = Path.Combine(_root, "Music", "Élan");
        Directory.Move(Path.GetDirectoryName(song)!, renamed);
        string renamedSong = Path.Combine(renamed, "song.flac");
        File.SetLastWriteTimeUtc(renamedSong, SecondModified);
        SaveLikeTagEditor(renamedSong);
        Assert.Equal(2, _database.GetTracks().Count);

        // Tag-nya lalu diubah aplikasi lain
        CopyFixture("second.flac", Path.Combine("Music", "Élan"), ThirdModified);
        var result = _scanner.Scan();

        Assert.Equal("Second Title", _database.GetTracks().Single(t => t.Id == id).Title);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, _scanner.Scan().Updated); // stempelnya sudah sama, jadi tidak dibaca lagi setiap scan
    }

    [Fact]
    public void Scan_TagsSavedWhileScanning_AreNotOverwrittenWithTheOldTags()
    {
        string song = CopyFixture("first.flac", "Music", FirstModified);
        _database.AddFolder(Path.GetDirectoryName(song)!);
        _scanner.Scan();

        // Filenya berubah, jadi scan ulang membaca tag lamanya. Setelah itu, tepat sebelum hasil scan disimpan, tag barunya
        // disimpan di editor tag
        File.SetLastWriteTimeUtc(song, SecondModified);
        var result = _scanner.Scan(new InlineProgress(p =>
        {
            if (p.Stage == ScanStage.Saving)
            {
                CopyFixture("second.flac", "Music", ThirdModified);
                SaveLikeTagEditor(song);
            }
        }));

        var track = Assert.Single(_database.GetTracks());
        Assert.Equal("Second Title", track.Title);
        Assert.Equal(ThirdModified.Ticks, track.ModifiedUtcTicks);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Unchanged);

        // Stempel di database sama dengan file, jadi scan berikutnya tidak perlu membacanya lagi
        var next = _scanner.Scan();
        Assert.Equal(0, next.Updated);
        Assert.Equal(1, next.Unchanged);
    }

    [Fact]
    public void Scan_NewSongTaggedWhileScanning_KeepsTheSavedTags()
    {
        string folder = Path.Combine(_root, "Music");
        Directory.CreateDirectory(folder);
        _database.AddFolder(folder);
        string song = CopyFixture("first.flac", "Music", FirstModified);

        var result = _scanner.Scan(new InlineProgress(p =>
        {
            if (p.Stage == ScanStage.Saving)
            {
                CopyFixture("second.flac", "Music", SecondModified);
                SaveLikeTagEditor(song);
            }
        }));

        var track = Assert.Single(_database.GetTracks());
        Assert.Equal("Second Title", track.Title);
        Assert.Equal(SecondModified.Ticks, track.ModifiedUtcTicks);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Unchanged);
    }

    /// <summary>Menyalin fixture ke folder musik sementara sebagai song.flac dengan waktu ubah tertentu.</summary>
    private string CopyFixture(string fixture, string folderName, DateTime modifiedUtc)
    {
        string folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "song.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Scanner", fixture), path, overwrite: true);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    /// <summary>Seperti TagEditService setelah menulis file: tag-nya dibaca ulang dari file lalu disimpan ke database.</summary>
    private void SaveLikeTagEditor(string path)
    {
        var track = TagReader.Read(path, FileStamp.From(new FileInfo(path)), out string? error);
        Assert.Null(error);
        _database.UpsertTracks([track]);
    }

    // Seperti aplikasi lain yang membuka file tanpa berbagi: TagLib tidak bisa membukanya, tapi ukuran dan waktu ubahnya
    // tetap terbaca
    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    /// <summary>Melapor langsung di thread scan (Progress&lt;T&gt; menundanya ke thread lain), supaya tes bisa menyela di tahap tertentu.</summary>
    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}

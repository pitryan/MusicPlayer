using Microsoft.Data.Sqlite;

namespace MusicPlayer.Library.Tests;

/// <summary>
/// Tes LibraryDatabase.RelocateFolder: folder musik yang dipindah ke tempat lain tetap membawa riwayat putar, favorit,
/// isi playlist, dan hasil pemeriksaan kualitasnya. Memakai database dan folder sementara seperti LibraryScannerTests.
/// </summary>
public sealed class FolderRelocationTests : IDisposable
{
    private static readonly DateTime FirstModified = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondModified = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.Tests", Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _database;
    private readonly LibraryScanner _scanner;

    public FolderRelocationTests()
    {
        _database = new LibraryDatabase(Path.Combine(_root, "library.db"));
        _scanner = new LibraryScanner(_database);
    }

    public void Dispose()
    {
        // Lihat LibraryScannerTests.Dispose
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
    public void MovedFolder_KeepsTheSongWithItsHistoryFavouritePlaylistAndQualityCheck()
    {
        string song = CopyFixture("first.flac", Path.Combine("Old", "Album"), FirstModified);
        string oldFolder = Path.Combine(_root, "Old");
        _database.AddFolder(oldFolder);
        _scanner.Scan();
        var before = Assert.Single(_database.GetTracks());
        _database.RecordPlay(song, SecondModified);
        _database.SetFavourite(song, true, SecondModified);
        long playlist = _database.CreatePlaylist("Mix", SecondModified);
        _database.AddToPlaylist(playlist, [song], SecondModified);
        var stamp = FileStamp.From(new FileInfo(song));
        _database.SaveQualityReport(song, stamp, Report(), SecondModified);

        string newFolder = Path.Combine(_root, "New");
        Directory.Move(oldFolder, newFolder);
        string moved = Path.Combine(newFolder, "Album", "song.flac");

        Assert.Equal(new RelocationPreview(1, 1), _database.PreviewRelocation(oldFolder, newFolder));
        var result = _database.RelocateFolder(oldFolder, newFolder);

        Assert.Equal(new RelocateFolderResult(RelocateFolderStatus.Relocated, LibraryPaths.NormalizeFolder(newFolder), 1), result);
        Assert.Equal([LibraryPaths.NormalizeFolder(newFolder)], _database.GetFolders());
        var track = Assert.Single(_database.GetTracks());
        Assert.Equal(before.Id, track.Id);
        Assert.Equal(moved, track.FilePath);
        Assert.Equal(Path.Combine(newFolder, "Album"), track.FolderPath);
        Assert.Equal(moved, Assert.Single(_database.GetMostPlayed()).Track.FilePath);
        Assert.Equal([moved], _database.GetFavouritePaths());
        Assert.Equal([moved], _database.GetPlaylistPaths(playlist));
        Assert.NotNull(_database.GetQualityReport(moved, stamp));

        // File-nya tidak berubah, jadi scan berikutnya tidak perlu membacanya lagi
        var scan = _scanner.Scan();
        Assert.Equal((0, 0, 0, 1), (scan.Added, scan.Updated, scan.Removed, scan.Unchanged));
    }

    [Fact]
    public void NewLocationScannedFirst_KeepsOneSongWithTheDateItWasFirstAdded()
    {
        string song = CopyFixture("first.flac", "Old", FirstModified);
        string oldFolder = Path.GetDirectoryName(song)!;
        _database.AddFolder(oldFolder);
        _scanner.Scan();
        _database.SetFavourite(song, true, FirstModified);

        // Folder dipindah, lalu lokasi barunya ditambahkan dan di-scan sebelum folder lamanya dipindah di library: lagu
        // yang sama tersimpan dua kali (folder lama tidak tersedia, jadi lagunya tidak dihapus)
        string newFolder = Path.Combine(_root, "New");
        Directory.Move(oldFolder, newFolder);
        string moved = Path.Combine(newFolder, "song.flac");
        File.SetLastWriteTimeUtc(moved, SecondModified);
        _database.AddFolder(newFolder);
        _scanner.Scan();
        _database.SetFavourite(moved, true, SecondModified);
        Assert.Equal(2, _database.GetTracks().Count);

        _database.RelocateFolder(oldFolder, newFolder);

        Assert.Equal(moved, Assert.Single(_database.GetTracks()).FilePath);
        // Tanggal yang dipakai Recently Added
        Assert.Equal(FirstModified, Assert.Single(_database.GetAlbums()).AddedUtc);
        Assert.Equal([moved], _database.GetFavouritePaths());
        Assert.Equal([LibraryPaths.NormalizeFolder(newFolder)], _database.GetFolders());
    }

    [Fact]
    public void MovedIntoAnotherLibraryFolder_JoinsThatFolder()
    {
        string song = CopyFixture("first.flac", "Old", FirstModified);
        string other = CopyFixture("second.flac", "Archive", FirstModified);
        string oldFolder = Path.GetDirectoryName(song)!;
        string archive = Path.GetDirectoryName(other)!;
        _database.AddFolder(oldFolder);
        _database.AddFolder(archive);
        _scanner.Scan();

        string newFolder = Path.Combine(archive, "Old");
        Directory.Move(oldFolder, newFolder);
        _database.RelocateFolder(oldFolder, newFolder);

        Assert.Equal([LibraryPaths.NormalizeFolder(archive)], _database.GetFolders());
        Assert.Contains(_database.GetTracks(), t => t.FilePath == Path.Combine(newFolder, "song.flac"));
        Assert.Equal(0, _scanner.Scan().Removed);
        Assert.Equal(2, _database.CountTracks());
    }

    [Fact]
    public void NonAsciiCaseOnlyRename_MovesTheSongToTheNewName()
    {
        // NOCASE di SQLite hanya melipat huruf ASCII, jadi "élan" dan "Élan" dianggap path yang berbeda
        string song = CopyFixture("first.flac", "élan", FirstModified);
        string oldFolder = Path.GetDirectoryName(song)!;
        _database.AddFolder(oldFolder);
        _scanner.Scan();
        _database.RecordPlay(song, SecondModified);

        string newFolder = Path.Combine(_root, "Élan");
        Directory.Move(oldFolder, newFolder);
        var result = _database.RelocateFolder(oldFolder, newFolder);

        Assert.Equal(RelocateFolderStatus.Relocated, result.Status);
        Assert.Equal(Path.Combine(newFolder, "song.flac"), Assert.Single(_database.GetTracks()).FilePath);
        Assert.Single(_database.GetMostPlayed());
    }

    [Fact]
    public void InvalidRequests_ChangeNothing()
    {
        string song = CopyFixture("first.flac", "Music", FirstModified);
        string folder = Path.GetDirectoryName(song)!;
        _database.AddFolder(folder);
        _scanner.Scan();

        Assert.Equal(RelocateFolderStatus.SameFolder, _database.RelocateFolder(folder, folder).Status);
        Assert.Equal(RelocateFolderStatus.NotFound, _database.RelocateFolder(folder, Path.Combine(_root, "Missing")).Status);
        Assert.Equal(RelocateFolderStatus.NotInLibrary, _database.RelocateFolder(Path.Combine(_root, "Other"), _root).Status);

        Assert.Equal([LibraryPaths.NormalizeFolder(folder)], _database.GetFolders());
        Assert.Equal(song, Assert.Single(_database.GetTracks()).FilePath);
    }

    [Theory]
    [InlineData(@"D:\Musik\Jepang\a.flac", @"D:\Musik\", @"E:\Lagu\", @"E:\Lagu\Jepang\a.flac")]
    [InlineData(@"D:\Musik", @"D:\Musik\", @"E:\Lagu\", @"E:\Lagu")]
    [InlineData(@"D:\a.flac", @"D:\", @"E:\Lagu\", @"E:\Lagu\a.flac")]
    [InlineData(@"D:\", @"D:\", @"E:\Lagu\", @"E:\Lagu")]
    [InlineData(@"D:\Musik", @"D:\Musik\", @"E:\", @"E:\")]
    public void Relocate_MapsPathsAndFolderColumns(string path, string oldFolder, string newFolder, string expected) =>
        Assert.Equal(expected, LibraryPaths.Relocate(path, oldFolder, newFolder));

    private static QualityReport Report() =>
        new(44100, 16, false, 16, null, SourceVerdict.Unknown, null, []);

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
}

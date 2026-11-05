using Microsoft.Data.Sqlite;

namespace MusicPlayer.Library.Tests;

/// <summary>
/// File yang dibuka dari luar aplikasi (Open with, drag & drop): file dan folder menjadi daftar lagu, memakai data
/// library kalau lagunya ada di sana. Memakai fixture first.flac dan second.flac seperti LibraryScannerTests.
/// </summary>
public sealed class AudioFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.Tests", Guid.NewGuid().ToString("N"));

    public AudioFilesTests() => Directory.CreateDirectory(_root);

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
    public void Expand_TakesAudioFilesFromFilesAndFolders_SortedByPath_EachOnce()
    {
        string album = Path.Combine(_root, "Album");
        string second = Copy("second.flac", Path.Combine("Album", "02 second.flac"));
        string first = Copy("first.flac", Path.Combine("Album", "01 first.flac"));
        string inSubfolder = Copy("first.flac", Path.Combine("Album", "Disc 2", "01 again.flac"));
        File.WriteAllText(Path.Combine(album, "cover.jpg"), "not audio");
        File.WriteAllText(Path.Combine(album, "._01 first.flac"), "macOS junk");
        string loose = Copy("second.flac", "loose.flac");

        // Urutan drop acak, folder dan file-nya sendiri sama-sama di-drop, ditambah path yang sudah tidak ada
        var files = AudioFiles.Expand([loose, album, second, Path.Combine(_root, "gone.flac"), Path.Combine(album, "cover.jpg")]);

        Assert.Equal([first, second, inSubfolder, loose], files);
    }

    [Fact]
    public void ToTracks_UsesTheLibrarySong_OrReadsTheTagsOfOtherFiles()
    {
        string inLibrary = Copy("first.flac", Path.Combine("Library", "first.flac"));
        string outside = Copy("second.flac", Path.Combine("Downloads", "second.flac"));
        var database = new LibraryDatabase(Path.Combine(_root, "library.db"));
        database.AddFolder(Path.GetDirectoryName(inLibrary)!);
        new LibraryScanner(database).Scan();
        long libraryId = Assert.Single(database.GetTracks()).Id;

        var tracks = AudioFiles.ToTracks(database, [outside, inLibrary, Path.Combine(_root, "gone.flac")]);

        Assert.Equal([outside, inLibrary], tracks.Select(t => t.FilePath));
        Assert.Equal("Second Title", tracks[0].Title); // dari tag, karena tidak ada di library
        Assert.Equal(0, tracks[0].Id);
        Assert.Equal(libraryId, tracks[1].Id);
        Assert.Single(database.GetTracks()); // file di luar library tidak ditambahkan
    }

    [Theory]
    [InlineData("song.FLAC", true)]
    [InlineData("song.m4a", true)]
    [InlineData("song.ogg", false)]
    [InlineData("._song.flac", false)]
    [InlineData("cover.jpg", false)]
    public void IsAudioFile_KnowsTheSupportedFormats(string name, bool expected) =>
        Assert.Equal(expected, AudioFiles.IsAudioFile(Path.Combine(_root, name)));

    [Fact]
    public void MayAffectLibrary_AudioFilesAndFolders_ButNotOtherFiles()
    {
        // Nama folder bertitik, seperti album "SEKAI ALBUM vol.3": tetap dikenali sebagai folder
        string album = Path.Combine(_root, "SEKAI ALBUM vol.3");
        Directory.CreateDirectory(album);
        string song = Path.Combine(album, "01 song.flac");
        string cover = Path.Combine(album, "cover.jpg");
        string lyrics = Path.Combine(album, "01 song.lrc");

        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Created, song));
        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Created, album)); // folder yang dipindah ke sini
        Assert.False(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Created, cover));

        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Changed, song)); // tag diubah aplikasi lain
        Assert.False(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Changed, lyrics)); // disimpan editor lirik
        Assert.False(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Changed, album));

        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Deleted, cover)); // bisa saja itu folder

        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Renamed, album, Path.Combine(_root, "old vol.2")));
        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Renamed, song, Path.Combine(album, "download.tmp")));
        Assert.True(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Renamed, Path.Combine(album, "x.tmp"), song));
        Assert.False(AudioFiles.MayAffectLibrary(WatcherChangeTypes.Renamed, cover, Path.Combine(album, "folder.jpg")));
    }

    private string Copy(string fixture, string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Scanner", fixture), path);
        return path;
    }
}

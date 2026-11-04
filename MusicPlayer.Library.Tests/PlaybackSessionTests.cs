using Microsoft.Data.Sqlite;

namespace MusicPlayer.Library.Tests;

/// <summary>File sesi (PlaybackSessionFile) dan pencarian lagu-lagunya di database saat sesi dipulihkan.</summary>
public sealed class PlaybackSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.Tests", Guid.NewGuid().ToString("N"));
    private string SessionPath => Path.Combine(_root, "session.json");

    public PlaybackSessionTests() => Directory.CreateDirectory(_root);

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
    public void SaveThenLoad_GivesTheSameSession()
    {
        var session = new PlaybackSession(
            @"D:\music\Japan\心做し.flac",
            TimeSpan.FromMilliseconds(123_456),
            [@"D:\music\a.flac", @"D:\music\b.flac"],
            [new SessionEntry(@"D:\music\c.flac", false, 1), new SessionEntry(@"D:\music\d.flac", true, -1)]);

        PlaybackSessionFile.Save(SessionPath, session);
        var loaded = PlaybackSessionFile.Load(SessionPath)!;

        Assert.Equal(session.Current, loaded.Current);
        Assert.Equal(session.Position, loaded.Position);
        Assert.Equal(session.Previous, loaded.Previous);
        Assert.Equal(session.UpNext, loaded.UpNext);
        Assert.False(File.Exists(SessionPath + ".tmp"));
    }

    [Fact]
    public void SavingNoSession_RemovesTheSavedOne()
    {
        PlaybackSessionFile.Save(SessionPath, new PlaybackSession(@"D:\music\a.flac", TimeSpan.Zero, [], []));

        PlaybackSessionFile.Save(SessionPath, null);

        Assert.False(File.Exists(SessionPath));
        Assert.Null(PlaybackSessionFile.Load(SessionPath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":2,\"current\":\"a\",\"positionMs\":0,\"previous\":[],\"upNext\":[]}")]
    public void DamagedOrNewerFile_IsIgnored(string content)
    {
        File.WriteAllText(SessionPath, content);

        Assert.Null(PlaybackSessionFile.Load(SessionPath));
    }

    [Fact]
    public void GetTracksByPath_FindsSongsStillInTheLibrary_IgnoringCase()
    {
        var database = new LibraryDatabase(Path.Combine(_root, "library.db"));
        string folder = Path.Combine(_root, "Music");
        Directory.CreateDirectory(folder);
        database.AddFolder(folder);

        // Lebih dari satu kelompok query (500 path per query)
        var tracks = Enumerable.Range(0, 1200)
            .Select(i => new Track { FilePath = Path.Combine(folder, $"song{i}.flac"), FolderPath = folder, Title = $"Song {i}" })
            .ToList();
        database.UpsertTracks(tracks);

        var wanted = tracks.Select(t => t.FilePath.ToUpperInvariant()).Append(Path.Combine(folder, "deleted.flac"));
        var found = database.GetTracksByPath(wanted);

        Assert.Equal(1200, found.Count);
        Assert.Equal("Song 1199", found[tracks[1199].FilePath].Title);
        Assert.False(found.ContainsKey(Path.Combine(folder, "deleted.flac")));
    }
}

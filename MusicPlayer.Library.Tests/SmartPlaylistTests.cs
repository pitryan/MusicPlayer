using Microsoft.Data.Sqlite;

namespace MusicPlayer.Library.Tests;

/// <summary>
/// Smart playlist: aturan yang dicocokkan dengan lagu (SmartPlaylists.Evaluate), penyimpanannya sebagai JSON, dan isinya
/// lewat LibraryDatabase. Lagu dibuat langsung tanpa file; database memakai folder sementara seperti LibraryScannerTests.
/// </summary>
public sealed class SmartPlaylistTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Track Lemon = Song("Lemon", artist: "米津玄師", album: "Lemon", year: 2018, added: Now.AddDays(-400));
    private static readonly Track Haru = Song("春泥棒", artist: "ヨルシカ", album: "創作", year: 2021, added: Now.AddDays(-3),
        folder: @"C:\Music\Japan\Yorushika", bits: 24, rate: 96000);
    private static readonly Track Hareru = Song("晴る", artist: "ヨルシカ", album: "晴る", year: 2024, added: Now.AddDays(-1),
        folder: @"C:\Music\Japan\Yorushika", lyrics: true);
    private static readonly Track Mp3 = Song("Old Song", artist: "Someone", album: null, year: null, added: Now.AddDays(-30),
        folder: @"C:\Music\Japan2", codec: "MP3", bitrate: 320, bits: 0, minutes: 2.5);

    private static readonly Track[] Library = [Lemon, Haru, Hareru, Mp3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.Tests", Guid.NewGuid().ToString("N"));

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
    public void Text_IgnoresCaseKanaAndWidth()
    {
        // Hiragana cocok dengan katakana, huruf lebar dengan huruf biasa, besar dengan kecil
        Assert.Equal([Haru, Hareru], Titles(Rule(SmartField.Artist, SmartOperator.Is, "よるしか")));
        Assert.Equal([Lemon], Titles(Rule(SmartField.Title, SmartOperator.Contains, "ＬＥＭ")));
        Assert.Equal([Mp3], Titles(Rule(SmartField.Format, SmartOperator.Is, "mp3")));
        Assert.Equal([Lemon, Mp3], Titles(Rule(SmartField.Artist, SmartOperator.DoesNotContain, "ヨルシカ")));
        Assert.Equal([Haru], Titles(Rule(SmartField.Title, SmartOperator.StartsWith, "春")));
    }

    [Fact]
    public void Text_EmptyValue_IsMissingTag()
    {
        // "Album is" kosong: lagu tanpa tag album
        Assert.Equal([Mp3], Titles(Rule(SmartField.Album, SmartOperator.Is, "")));
    }

    [Fact]
    public void MatchAll_NeedsEveryRule_MatchAny_NeedsOne()
    {
        SmartRule[] rules = [new(SmartField.Artist, SmartOperator.Is, "ヨルシカ"), new(SmartField.Year, SmartOperator.GreaterThan, "2022")];

        Assert.Equal([Hareru], Evaluate(new SmartPlaylistRules(MatchAll: true, rules)));
        Assert.Equal([Haru, Hareru], Evaluate(new SmartPlaylistRules(MatchAll: false, rules)));
    }

    [Fact]
    public void Numbers_UseTheUnitsShown_AndMissingValuesOnlyMatchIsNot()
    {
        Assert.Equal([Haru], Titles(Rule(SmartField.SampleRate, SmartOperator.Is, "96")));
        Assert.Equal([Lemon, Hareru, Mp3], Titles(Rule(SmartField.SampleRate, SmartOperator.Is, "44,1"))); // koma juga diterima
        Assert.Equal([Haru], Titles(Rule(SmartField.BitDepth, SmartOperator.GreaterThan, "16")));
        Assert.Equal([Haru, Hareru], Titles(Rule(SmartField.Year, SmartOperator.GreaterThan, "2020")));

        // Lagu tanpa tahun tidak "sebelum 2020", tapi memang "bukan 2018"
        Assert.Equal([Lemon], Titles(Rule(SmartField.Year, SmartOperator.LessThan, "2020")));
        Assert.Equal([Haru, Hareru, Mp3], Titles(Rule(SmartField.Year, SmartOperator.IsNot, "2018")));
        Assert.Equal([Lemon, Haru, Hareru], Titles(Rule(SmartField.Duration, SmartOperator.GreaterThan, "3.5")));
    }

    [Fact]
    public void Dates_CountDaysBackFromNow_NeverPlayedIsNotRecent()
    {
        var context = Context(plays: new() { [Lemon.FilePath] = new PlayStats(5, Now.AddDays(-2)), [Haru.FilePath] = new PlayStats(1, Now.AddDays(-60)) });

        Assert.Equal([Haru, Hareru], Titles(Rule(SmartField.DateAdded, SmartOperator.InTheLast, "7")));
        Assert.Equal([Lemon], Titles(Rule(SmartField.LastPlayed, SmartOperator.InTheLast, "30"), context));
        Assert.Equal([Haru, Hareru, Mp3], Titles(Rule(SmartField.LastPlayed, SmartOperator.NotInTheLast, "30"), context));
    }

    [Fact]
    public void Folder_IncludesSubfolders_ButNotFoldersThatOnlyStartTheSame()
    {
        // "Japan2" bukan bagian dari "Japan"
        Assert.Equal([Haru, Hareru], Titles(Rule(SmartField.Folder, SmartOperator.Is, @"C:\Music\Japan")));
        Assert.Equal([Haru, Hareru], Titles(Rule(SmartField.Folder, SmartOperator.Is, @"c:\music\japan\")));
        Assert.Equal([Lemon, Mp3], Titles(Rule(SmartField.Folder, SmartOperator.IsNot, @"C:\Music\Japan")));
    }

    [Fact]
    public void QualityFavouriteAndLyrics()
    {
        var context = Context(favourites: [Lemon.FilePath.ToUpperInvariant()]);

        Assert.Equal([Haru], Titles(Rule(SmartField.Quality, SmartOperator.Is, "hiRes")));
        Assert.Equal([Mp3], Titles(Rule(SmartField.Quality, SmartOperator.Is, "lossy")));
        Assert.Equal([Lemon, Haru, Hareru], Titles(Rule(SmartField.Quality, SmartOperator.IsNot, "lossy")));
        Assert.Equal([Lemon], Titles(Rule(SmartField.Favourite, SmartOperator.Is), context));
        Assert.Equal([Haru, Hareru, Mp3], Titles(Rule(SmartField.Favourite, SmartOperator.IsNot), context));
        Assert.Equal([Hareru], Titles(Rule(SmartField.HasLyrics, SmartOperator.Is)));
    }

    [Fact]
    public void NoRules_EverySong_OrderAndLimitPickTheTop()
    {
        var context = Context(plays: new()
        {
            [Mp3.FilePath] = new PlayStats(9, Now.AddDays(-5)),
            [Hareru.FilePath] = new PlayStats(9, Now.AddDays(-1)), // sama banyak: yang terakhir diputar lebih dulu
            [Lemon.FilePath] = new PlayStats(2, Now.AddDays(-9)),
        });

        Assert.Equal([Hareru, Mp3], Evaluate(new SmartPlaylistRules(true, [], SmartOrder.MostPlayed, Limit: 2), context));
        Assert.Equal([Hareru, Haru, Mp3], Evaluate(new SmartPlaylistRules(true, [], SmartOrder.RecentlyAdded, Limit: 3), context));
        Assert.Equal([Hareru, Mp3, Lemon, Haru], Evaluate(new SmartPlaylistRules(true, [], SmartOrder.RecentlyPlayed), context));
        Assert.Equal(4, Evaluate(new SmartPlaylistRules(true, [], SmartOrder.Random), context).Count);
    }

    [Fact]
    public void Album_OrderIsByAlbumArtistAlbumDiscAndTrack()
    {
        var a2 = Song("B", artist: "X", album: "Album", track: 2);
        var a1 = Song("A", artist: "X", album: "Album", track: 1, disc: 1);
        var b1 = Song("C", artist: "X", album: "Album", track: 1, disc: 2);
        var other = Song("D", artist: "A Artist", album: "Zeta", track: 9);

        var result = SmartPlaylists.Evaluate(new SmartPlaylistRules(true, []), [b1, a2, other, a1], Context());

        Assert.Equal([other, a1, a2, b1], result);
    }

    [Fact]
    public void IncompleteRule_MatchesNothing()
    {
        var rule = new SmartRule(SmartField.Year, SmartOperator.GreaterThan, "");
        Assert.False(SmartPlaylists.IsComplete(rule));
        Assert.Empty(Titles(rule));

        // Operator yang tidak berlaku untuk data ini
        Assert.False(SmartPlaylists.IsComplete(new SmartRule(SmartField.Title, SmartOperator.InTheLast, "7")));
        Assert.False(SmartPlaylists.IsComplete(new SmartRule(SmartField.Duration, SmartOperator.Is, "4")));
    }

    [Fact]
    public void Json_RoundTrips_AndSkipsRulesItDoesNotKnow()
    {
        var rules = new SmartPlaylistRules(false,
            [new(SmartField.Artist, SmartOperator.Contains, "ヨルシカ \"live\""), new(SmartField.Folder, SmartOperator.Is, @"D:\music\Japan")],
            SmartOrder.MostPlayed, Limit: 25);

        var loaded = SmartPlaylists.FromJson(SmartPlaylists.ToJson(rules))!;
        Assert.False(loaded.MatchAll);
        Assert.Equal(rules.Rules, loaded.Rules);
        Assert.Equal(SmartOrder.MostPlayed, loaded.Order);
        Assert.Equal(25, loaded.Limit);

        // Data dari versi aplikasi yang lebih baru dilewati, bukan membuat seluruh playlist gagal dibaca
        var newer = SmartPlaylists.FromJson("""{"version":2,"match":"all","rules":[{"field":"bpm","operator":"greaterThan","value":"120"},{"field":"genre","operator":"is","value":"Rock"}]}""")!;
        Assert.Equal([new SmartRule(SmartField.Genre, SmartOperator.Is, "Rock")], newer.Rules);
        Assert.Null(newer.Limit);

        Assert.Null(SmartPlaylists.FromJson("not json"));
        Assert.Null(SmartPlaylists.FromJson("""{"version":1}"""));
    }

    [Fact]
    public void Database_SmartPlaylistFollowsTheLibrary_RegularOnesStayAsTheyWere()
    {
        var database = LibraryIn(out var moved);
        var (lemon, haru, hareru, mp3) = (moved(Lemon), moved(Haru), moved(Hareru), moved(Mp3));
        database.UpsertTracks([lemon, haru, hareru, mp3]);
        long regular = database.CreatePlaylist("Mix", Now);
        database.AddToPlaylist(regular, [lemon.FilePath], Now);
        long smart = database.CreateSmartPlaylist("Yorushika", new SmartPlaylistRules(true,
            [new(SmartField.Artist, SmartOperator.Is, "ヨルシカ")], SmartOrder.RecentlyAdded), Now);

        var playlists = database.GetPlaylists();
        var smartSummary = Assert.Single(playlists, p => p.Id == smart);
        Assert.True(smartSummary.IsSmart);
        Assert.Equal(2, smartSummary.TrackCount);
        Assert.Equal([hareru.FilePath, haru.FilePath], smartSummary.CoverTrackPaths); // dua album berbeda
        Assert.False(Assert.Single(playlists, p => p.Id == regular).IsSmart);

        // Tanggal ditambahkan dibaca dari database, jadi urutan "baru ditambahkan" benar
        Assert.Equal([hareru.FilePath, haru.FilePath], database.GetPlaylistEntries(smart).Select(e => e.Track.FilePath));
        Assert.Equal([lemon.FilePath], database.GetPlaylistEntries(regular).Select(e => e.Track.FilePath));

        // Lagu baru di library langsung ikut masuk
        var newSong = moved(Song("又三郎", artist: "ヨルシカ", album: "創作", added: Now));
        database.UpsertTracks([newSong]);
        Assert.Equal(newSong.FilePath, database.GetPlaylistEntries(smart)[0].Track.FilePath);

        database.UpdateSmartPlaylist(smart, "Lossy", new SmartPlaylistRules(true, [new(SmartField.Quality, SmartOperator.Is, "lossy")]), Now);
        database.UpdateSmartPlaylist(regular, "Ignored", new SmartPlaylistRules(true, []), Now);

        Assert.Equal("Lossy", database.GetPlaylists().Single(p => p.Id == smart).Name);
        Assert.Equal([mp3.FilePath], database.GetPlaylistEntries(smart).Select(e => e.Track.FilePath));
        Assert.Equal("Mix", database.GetPlaylists().Single(p => p.Id == regular).Name);
        Assert.Null(database.GetSmartPlaylistRules(regular));
    }

    [Fact]
    public void Database_FromBeforeSmartPlaylists_GetsTheRulesColumn()
    {
        string path = Path.Combine(_root, "library.db");
        new LibraryDatabase(path).CreatePlaylist("Old", Now);

        // Database versi 3: kolom rules belum ada
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE playlists DROP COLUMN rules; PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var database = new LibraryDatabase(path);
        long smart = database.CreateSmartPlaylist("New", new SmartPlaylistRules(true, []), Now);

        Assert.Equal([false, true], database.GetPlaylists().OrderBy(p => p.Id).Select(p => p.IsSmart));
        Assert.NotNull(database.GetSmartPlaylistRules(smart));
    }

    private static SmartRule Rule(SmartField field, SmartOperator op, string value = "") => new(field, op, value);

    // Lagu yang cocok, dalam urutan Library (urutan hasilnya sendiri diuji terpisah)
    private static IReadOnlyList<Track> Titles(SmartRule rule, SmartPlaylistContext? context = null)
    {
        var matching = Evaluate(new SmartPlaylistRules(true, [rule]), context);
        return Library.Where(matching.Contains).ToList();
    }

    private static IReadOnlyList<Track> Evaluate(SmartPlaylistRules rules, SmartPlaylistContext? context = null) =>
        SmartPlaylists.Evaluate(rules, Library, context ?? Context());

    // Lagu yang sama di dalam folder library sementara: UpsertTracks membuang lagu di luar folder library
    private LibraryDatabase LibraryIn(out Func<Track, Track> moved)
    {
        string music = Path.Combine(_root, "Music");
        Directory.CreateDirectory(music);
        var database = new LibraryDatabase(Path.Combine(_root, "library.db"));
        database.AddFolder(music);
        moved = track => track with
        {
            FilePath = music + track.FilePath[@"C:\Music".Length..],
            FolderPath = music + track.FolderPath[@"C:\Music".Length..],
        };
        return database;
    }

    private static SmartPlaylistContext Context(Dictionary<string, PlayStats>? plays = null, string[]? favourites = null) =>
        new(plays ?? [], new HashSet<string>(favourites ?? [], StringComparer.OrdinalIgnoreCase), Now, new Random(1));

    private static Track Song(string title, string? artist = "Artist", string? album = "Album", int? year = 2020,
        DateTime? added = null, string folder = @"C:\Music", string codec = "FLAC", int bits = 16, int rate = 44100,
        int bitrate = 900, bool lyrics = false, int? track = null, int? disc = null, double minutes = 4) =>
        new()
        {
            FilePath = Path.Combine(folder, title + "." + codec.ToLowerInvariant()),
            FolderPath = folder,
            Title = title,
            Artist = artist,
            Album = album,
            Year = year,
            Codec = codec,
            BitsPerSample = bits,
            SampleRate = rate,
            BitrateKbps = bitrate,
            HasLyrics = lyrics,
            TrackNumber = track,
            DiscNumber = disc,
            Duration = TimeSpan.FromMinutes(minutes),
            // Waktu ubah file berbeda dari tanggal ditambahkan, seperti setelah tag-nya diedit: "Date Added" harus memakai
            // tanggal ditambahkan yang tersimpan, bukan waktu ubah file
            ModifiedUtcTicks = Now.AddHours(-1).Ticks,
            AddedUtcTicks = (added ?? Now).Ticks,
        };
}

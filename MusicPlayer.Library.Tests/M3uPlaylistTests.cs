using System.Text;

namespace MusicPlayer.Library.Tests;

/// <summary>
/// Impor dan ekspor playlist M3U/M3U8 (M3uPlaylist): cara path ditulis aplikasi lain, encoding file lama, dan lagu yang
/// sudah pindah tempat sejak playlist-nya dibuat.
/// </summary>
public sealed class M3uPlaylistTests : IDisposable
{
    private static readonly Track Haru = Song(@"D:\music\Japan\Yorushika\創作\02 春泥棒.flac", "春泥棒", "ヨルシカ", 290);
    private static readonly Track Matasaburo = Song(@"D:\music\Japan\Yorushika\創作\01 又三郎.flac", "又三郎", "ヨルシカ", 201);
    private static readonly Track IntroA = Song(@"D:\music\Japan\A\Album One\01 Intro.flac", "Intro", "A", 60);
    private static readonly Track IntroB = Song(@"D:\music\Japan\B\Album Two\01 Intro.flac", "Intro", "B", 61);
    private static readonly Track[] Library = [Haru, Matasaburo, IntroA, IntroB];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "MusicPlayer.Tests", Guid.NewGuid().ToString("N"));

    public M3uPlaylistTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ParsePaths_UnderstandsTheWaysOtherAppsWritePaths()
    {
        string text = string.Join("\r\n",
            "#EXTM3U",
            "#EXTINF:290,ヨルシカ - 春泥棒",
            @"D:\music\Japan\Yorushika\創作\02 春泥棒.flac",
            "",
            "   ../Korean/song.flac   ", // relatif terhadap folder file M3U-nya, dengan garis miring
            "file:///D:/music/Japan/a%20b%20%E6%98%A5.flac",
            "http://radio.example.com/stream.mp3", // radio internet: bukan file
            "# komentar",
            @"D:\music\Japan\Yorushika\創作\02 春泥棒.flac"); // lagu yang sama dua kali tetap dua kali

        var paths = M3uPlaylist.ParsePaths(text, @"D:\music\Playlists");

        Assert.Equal(
        [
            @"D:\music\Japan\Yorushika\創作\02 春泥棒.flac",
            @"D:\music\Korean\song.flac",
            @"D:\music\Japan\a b 春.flac",
            @"D:\music\Japan\Yorushika\創作\02 春泥棒.flac",
        ], paths);
    }

    [Fact]
    public void Match_SamePath_IgnoringCase_KeepsOrderAndDuplicates()
    {
        var (tracks, missing, elsewhere) = M3uPlaylist.Match(
            [Matasaburo.FilePath.ToUpperInvariant(), Haru.FilePath, Matasaburo.FilePath, @"D:\music\gone.flac"], Library);

        Assert.Equal([Matasaburo, Haru, Matasaburo], tracks);
        Assert.Equal([@"D:\music\gone.flac"], missing);
        Assert.Equal(0, elsewhere);
    }

    [Fact]
    public void Match_FromAnotherComputerOrDrive_FindsTheSongByFileAndFolderName()
    {
        // Playlist dibuat di komputer lain (E:\Music, tanpa folder Japan), atau di Linux/Mac
        var (tracks, missing, elsewhere) = M3uPlaylist.Match(
            [@"E:\Music\Yorushika\創作\02 春泥棒.flac", @"C:\home\pit\Music\創作\01 又三郎.flac"], Library);

        Assert.Equal([Haru, Matasaburo], tracks);
        Assert.Empty(missing);
        Assert.Equal(2, elsewhere);
    }

    [Fact]
    public void Match_OnlyTheFileNameIsTheSame_IsNotAGuess()
    {
        // "01 Intro.flac" ada di dua album: hanya nama file yang sama tidak cukup untuk memilih salah satunya
        string other = @"E:\Other\Album Three\01 Intro.flac";
        string albumTwo = @"E:\Music\B\Album Two\01 Intro.flac";

        var (tracks, missing, _) = M3uPlaylist.Match([other, albumTwo], Library);

        Assert.Equal([IntroB], tracks);
        Assert.Equal([other], missing);

        // Juga kalau hanya ada satu lagu dengan nama file itu, tapi foldernya lain
        var (onlyName, _, _) = M3uPlaylist.Match([other], [IntroA]);
        Assert.Empty(onlyName);

        // Dua lagu sama cocoknya (album dengan nama yang sama dari dua artis): tidak dipilih salah satunya
        var greatestA = Song(@"D:\music\A\Greatest Hits\01 Intro.flac", "Intro", "A", 60);
        var greatestB = Song(@"D:\music\B\Greatest Hits\01 Intro.flac", "Intro", "B", 60);
        var (none, stillMissing, _) = M3uPlaylist.Match([@"E:\Greatest Hits\01 Intro.flac"], [greatestA, greatestB]);
        Assert.Empty(none);
        Assert.Single(stillMissing);
    }

    [Fact]
    public void Write_ThenRead_GivesTheSameSongsAndName()
    {
        string text = M3uPlaylist.Write([Haru, Matasaburo, Haru], "Yorushika Best");

        Assert.StartsWith("#EXTM3U\r\n#PLAYLIST:Yorushika Best\r\n#EXTINF:290,ヨルシカ - 春泥棒\r\n", text);
        Assert.Equal("Yorushika Best", M3uPlaylist.ReadTitle(text));
        Assert.Equal([Haru.FilePath, Matasaburo.FilePath, Haru.FilePath], M3uPlaylist.ParsePaths(text, _root));
    }

    [Fact]
    public void Export_IntoTheMusicFolder_WritesRelativePathsWithSlashes()
    {
        // Playlist disimpan di D:\music, lalu D:\music disalin ke kartu SD pemutar portabel
        var export = M3uPlaylist.Export([Haru, Matasaburo], "Mix", @"D:\music", _ => false);

        Assert.Contains("\r\nJapan/Yorushika/創作/02 春泥棒.flac\r\n", export.Text);
        Assert.Equal((2, 0), (export.Relative, export.Absolute));
        Assert.Equal([@"E:\Japan\Yorushika\創作\02 春泥棒.flac", @"E:\Japan\Yorushika\創作\01 又三郎.flac"],
            M3uPlaylist.ParsePaths(export.Text, @"E:\"));
    }

    [Fact]
    public void Export_ToACopyOnAnotherDrive_UsesTheCopy_TheLongestMatchFirst()
    {
        // Kartu SD (E:\) sudah berisi salinan folder musik: E:\music\Japan\... dan juga E:\創作\... (salinan album saja)
        var onCard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"E:\music\Japan\Yorushika\創作\02 春泥棒.flac",
            @"E:\創作\02 春泥棒.flac",
            @"E:\創作\01 又三郎.flac",
        };

        var export = M3uPlaylist.Export([Haru, Matasaburo, IntroA], null, @"E:\", onCard.Contains);

        Assert.Equal(
            [@"E:\music\Japan\Yorushika\創作\02 春泥棒.flac", @"E:\創作\01 又三郎.flac", IntroA.FilePath],
            M3uPlaylist.ParsePaths(export.Text, @"E:\"));
        Assert.Equal((2, 1), (export.Relative, export.Absolute)); // lagu yang tidak ada di kartu: path lengkap
    }

    [Fact]
    public void Export_SomewhereElse_KeepsFullPaths()
    {
        var export = M3uPlaylist.Export([Haru], null, @"C:\Users\pit\Documents", _ => false);

        Assert.Equal([Haru.FilePath], M3uPlaylist.ParsePaths(export.Text, @"C:\Users\pit\Documents"));
        Assert.Equal((0, 1), (export.Relative, export.Absolute));
    }

    [Fact]
    public void Write_TagsWithLineBreaks_StayOnTheirLine()
    {
        var broken = Haru with { Title = "春泥棒\r\nD:\\fake.flac" };

        var paths = M3uPlaylist.ParsePaths(M3uPlaylist.Write([broken]), _root);

        Assert.Equal([Haru.FilePath], paths);
    }

    [Fact]
    public void Import_OldShiftJisM3u_AndUtf8M3u8WithBom()
    {
        // .m3u lama dari Winamp atau foobar2000 di Windows Jepang: Shift-JIS, tanpa BOM
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string oldFile = Path.Combine(_root, "Old Mix.m3u");
        File.WriteAllBytes(oldFile, Encoding.GetEncoding(932).GetBytes($"{Haru.FilePath}\r\n{Matasaburo.FilePath}\r\n"));

        var old = M3uPlaylist.Import(oldFile, Library);
        Assert.Equal("Old Mix", old.Name);
        Assert.Equal([Haru, Matasaburo], old.Tracks);

        string newFile = Path.Combine(_root, "new.m3u8");
        File.WriteAllText(newFile, M3uPlaylist.Write([Matasaburo], "夜のプレイリスト") + "D:\\music\\gone.flac\r\n", new UTF8Encoding(true));

        var imported = M3uPlaylist.Import(newFile, Library);
        Assert.Equal("夜のプレイリスト", imported.Name);
        Assert.Equal([Matasaburo], imported.Tracks);
        Assert.Equal([@"D:\music\gone.flac"], imported.Missing);
    }

    private static Track Song(string path, string title, string artist, int seconds) => new()
    {
        FilePath = path,
        FolderPath = Path.GetDirectoryName(path)!,
        Title = title,
        Artist = artist,
        Duration = TimeSpan.FromSeconds(seconds),
    };
}

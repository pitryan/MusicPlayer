using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace MusicPlayer;

public partial class App : Application
{
    /// <summary>Jendela utama, dibutuhkan misalnya oleh FolderPicker.</summary>
    public static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>Database library yang dipakai bersama oleh semua halaman.</summary>
    public static LibraryDatabase Database { get; private set; } = null!;

    /// <summary>Pemutar dan antrean lagu yang dipakai bersama oleh semua halaman.</summary>
    public static PlaybackService Playback { get; private set; } = null!;

    /// <summary>Lirik lagu yang sedang diputar, dipakai bersama panel lirik dan layar penuh.</summary>
    public static LyricsController Lyrics { get; private set; } = null!;

    /// <summary>Lagu favorit (bintang ☆), dipakai bersama LCD, menu, dan semua daftar lagu.</summary>
    public static FavouritesService Favourites { get; private set; } = null!;

    /// <summary>Playlist buatan sendiri, dipakai bersama sidebar, menu, dan halaman playlist.</summary>
    public static PlaylistsService Playlists { get; private set; } = null!;

    /// <summary>Menyimpan hasil editor tag dan memberi tahu halaman lain bahwa isi library berubah.</summary>
    public static TagEditService TagEdits { get; private set; } = null!;

    /// <summary>
    /// Kontrol media Windows (tombol media keyboard/headset, panel media). Null kalau Windows tidak menyediakannya;
    /// aplikasi tetap jalan tanpanya.
    /// </summary>
    public static MediaControlsSync? MediaControls { get; private set; }

    /// <summary>Pemeriksa kualitas sumber (popup badge kualitas): hi-res asli, upsampled, atau dari sumber lossy.</summary>
    public static QualityCheckService QualityChecks { get; private set; } = null!;

    /// <summary>Antrean dan lagu terakhir, disimpan supaya bisa dilanjutkan saat aplikasi dibuka lagi.</summary>
    public static SessionService Session { get; private set; } = null!;

    /// <summary>Klien MusicBrainz untuk editor tag (Look up on MusicBrainz). Satu instance, supaya batas 1 permintaan per detik berlaku di seluruh aplikasi.</summary>
    public static MusicBrainzClient MusicBrainz { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

    private static ArtistInfoService? _artistInfo;

    /// <summary>
    /// Keterangan artis untuk halaman artis (About, asal, tanggal), dari MusicBrainz, Wikidata, dan Wikipedia. Memakai
    /// klien MusicBrainz yang sama dengan editor tag, supaya aturan 1 permintaan per detik berlaku untuk keduanya.
    /// </summary>
    public static ArtistInfoService ArtistInfo => _artistInfo ??= CreateArtistInfo();

    private static ArtistInfoService CreateArtistInfo()
    {
        // Foto artis yang sempat diunduh Stage 14a tidak dipakai lagi; folder-nya dibuang supaya tidak memakan ruang
        try
        {
            string oldPhotos = Path.Combine(ApplicationData.Current.LocalFolder.Path, "ArtistPhotos");
            if (Directory.Exists(oldPhotos))
                Directory.Delete(oldPhotos, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return new ArtistInfoService(Database, new ArtistInfoClient(MusicBrainz, http));
    }

    public App()
    {
        // Sebelum yang lain, supaya error saat aplikasi baru mulai juga tercatat
        CrashLog.Install(this);
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Aplikasi packaged punya folder data sendiri, terpisah dari database LibraryLab
        string databasePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "library.db");
        Database = new LibraryDatabase(databasePath);
        Favourites = new FavouritesService(Database);
        Playlists = new PlaylistsService(Database);
        Playback = new PlaybackService(DispatcherQueue.GetForCurrentThread(), Database);
        RestorePlaybackSettings();
        Lyrics = CreateLyrics();
        TagEdits = new TagEditService(Database, Playback, CoverCache.Invalidate, () => Lyrics.Retry());
        MediaControls = CreateMediaControls();
        QualityChecks = new QualityCheckService(Database, QualityCheckService.AnalyzeFile);

        // Sesi terakhir dipulihkan sebelum jendela dibuat, supaya lagunya langsung tampil. Setelah lirik dan kontrol media
        // Windows siap, supaya keduanya ikut menampilkan lagu itu. Kalau aplikasi dibuka dengan file ("Open with"), file
        // itulah yang diputar, jadi sesinya tidak dipulihkan.
        var openedFiles = FilesOf(AppInstance.GetCurrent().GetActivatedEventArgs());
        string sessionPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "session.json");
        Session = new SessionService(Playback, sessionPath, DispatcherQueue.GetForCurrentThread());
        Session.Start(Database, restore: openedFiles.Count == 0);

        MainWindow = new MainWindow();
        MainWindow.Activate();
        if (openedFiles.Count > 0)
            _ = FileOpener.OpenAsync(openedFiles);
    }

    /// <summary>
    /// Aplikasi dibuka lagi selagi sudah berjalan (lihat Program): jendelanya dimunculkan, dan file yang dibuka dengan
    /// "Open with" atau klik dua kali di Explorer langsung diputar. Dipanggil dari thread lain.
    /// </summary>
    public static void OnActivatedAgain(AppActivationArguments args)
    {
        var files = FilesOf(args);

        // Bisa terjadi sebelum jendela utama selesai dibuat; jendela itu tetap akan muncul sendiri
        if (MainWindow is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                window.BringToFront();
                if (files.Count > 0)
                    _ = FileOpener.OpenAsync(files);
            });
        }
    }

    /// <summary>Path file yang dibuka lewat aktivasi ini; kosong untuk peluncuran biasa.</summary>
    private static IReadOnlyList<string> FilesOf(AppActivationArguments args) =>
        args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs file
            ? file.Files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList()
            : [];

    private static MediaControlsSync? CreateMediaControls()
    {
        try
        {
            return new MediaControlsSync(Playback, new WindowsMediaControls(DispatcherQueue.GetForCurrentThread()), CoverArt.TryRead);
        }
        catch (Exception)
        {
            return null; // tanpa kontrol media, tombol di aplikasi dan shortcut keyboard tetap berfungsi
        }
    }

    /// <summary>
    /// Lirik dicari di tag, file .lrc, lalu LRCLIB. Hasil LRCLIB disimpan di folder Lyrics di samping database, jadi
    /// lagu yang sama tidak butuh internet lagi.
    /// </summary>
    private static LyricsController CreateLyrics()
    {
        string cacheFolder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Lyrics");
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var finder = new LyricsFinder(new LyricsCache(cacheFolder), new LrclibClient(http));
        return new LyricsController(Playback, finder);
    }

    /// <summary>
    /// Volume, mute, shuffle, repeat, dan pilihan output diingat dari sesi sebelumnya, lalu disimpan setiap kali berubah.
    /// </summary>
    private static void RestorePlaybackSettings()
    {
        // Belum ada lagu, jadi slider mengatur volume aplikasi: nilai ini menjadi AppVolume. Versi lama menyimpan nilai slider
        // di "Volume"/"Muted". Dalam exclusive mode nilai itu bisa volume DAC (lagu shared mode lalu lebih pelan sampai slider
        // digeser) atau volume digital perangkat tanpa volume hardware (tanpa nilai itu, lagu pertama langsung keras). Lebih
        // aman terlalu pelan, jadi nilai lama dipakai sampai volume aplikasi disimpan dengan kunci barunya.
        Playback.Volume = AppSettings.Get("AppVolume", AppSettings.Get("Volume", 100.0));
        Playback.IsMuted = AppSettings.Get("AppMuted", AppSettings.Get("Muted", false));

        // Batas awal volume dari sesi sebelumnya (volume DAC yang terakhir terdengar), -1 kalau tidak ada
        double startLimit = AppSettings.Get("StartVolumeLimit", -1.0);
        string mutedDevice = AppSettings.Get("StartMutedDeviceId", "");
        Playback.RestoreStartLimit(startLimit >= 0 ? startLimit : null, AppSettings.Get("StartMuted", false),
            mutedDevice.Length > 0 ? mutedDevice : null);
        Playback.IsShuffle = AppSettings.Get("Shuffle", false);
        Playback.RepeatMode = (RepeatMode)AppSettings.Get("Repeat", (int)RepeatMode.Off);

        // Autoplay: lagu mirip dari library (artis, genre, favorit, dekade), tanpa lagu yang baru diputar
        Playback.AutoplaySource = (seed, exclude, count) =>
        {
            foreach (var play in Database.GetRecentPlays(100))
                exclude.Add(play.Track.FilePath);
            return AutoplayPicker.Pick(seed, Database.GetTracks(), exclude, Favourites.Paths, count, Random.Shared);
        };
        Playback.AutoplayEnabled = AppSettings.Get("Autoplay", true);
        Playback.UseExclusiveMode = AppSettings.Get("ExclusiveMode", true);

        // ID kosong = "Windows default". Namanya ikut disimpan untuk pesan saat perangkat itu tidak tersambung.
        string outputId = AppSettings.Get("OutputDeviceId", "");
        Playback.SelectOutputDevice(outputId.Length > 0 ? outputId : null, AppSettings.Get("OutputDeviceName", ""));

        Playback.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                // Volume aplikasi, bukan nilai slider: dalam exclusive mode slider menampilkan volume DAC, dan nilai itu
                // tidak boleh dipakai lagi sebagai volume aplikasi
                case nameof(PlaybackService.AppVolume): AppSettings.Set("AppVolume", Playback.AppVolume); break;
                case nameof(PlaybackService.IsAppMuted): AppSettings.Set("AppMuted", Playback.IsAppMuted); break;
                case nameof(PlaybackService.StartVolumeLimit):
                    AppSettings.Set("StartVolumeLimit", Playback.StartVolumeLimit ?? -1.0);
                    AppSettings.Set("StartMuted", Playback.StartMutedLimit);
                    AppSettings.Set("StartMutedDeviceId", Playback.StartMutedDeviceId ?? "");
                    break;
                case nameof(PlaybackService.IsShuffle): AppSettings.Set("Shuffle", Playback.IsShuffle); break;
                case nameof(PlaybackService.RepeatMode): AppSettings.Set("Repeat", (int)Playback.RepeatMode); break;
                case nameof(PlaybackService.AutoplayEnabled): AppSettings.Set("Autoplay", Playback.AutoplayEnabled); break;
                case nameof(PlaybackService.UseExclusiveMode): AppSettings.Set("ExclusiveMode", Playback.UseExclusiveMode); break;
                case nameof(PlaybackService.OutputDeviceId):
                    AppSettings.Set("OutputDeviceId", Playback.OutputDeviceId ?? "");
                    AppSettings.Set("OutputDeviceName", Playback.OutputDeviceName);
                    break;
            }
        };
    }
}

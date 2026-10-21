using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
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

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Aplikasi packaged punya folder data sendiri, terpisah dari database LibraryLab
        string databasePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "library.db");
        Database = new LibraryDatabase(databasePath);
        Playback = new PlaybackService(DispatcherQueue.GetForCurrentThread(), Database);
        RestorePlaybackSettings();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>
    /// Volume, mute, shuffle, repeat, dan pilihan output diingat dari sesi sebelumnya, lalu disimpan setiap kali berubah.
    /// </summary>
    private static void RestorePlaybackSettings()
    {
        Playback.Volume = AppSettings.Get("Volume", 100.0);
        Playback.IsMuted = AppSettings.Get("Muted", false);
        Playback.IsShuffle = AppSettings.Get("Shuffle", false);
        Playback.RepeatMode = (RepeatMode)AppSettings.Get("Repeat", (int)RepeatMode.Off);
        Playback.UseExclusiveMode = AppSettings.Get("ExclusiveMode", true);

        // ID kosong = "Windows default". Namanya ikut disimpan untuk pesan saat perangkat itu tidak tersambung.
        string outputId = AppSettings.Get("OutputDeviceId", "");
        Playback.SelectOutputDevice(outputId.Length > 0 ? outputId : null, AppSettings.Get("OutputDeviceName", ""));

        Playback.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PlaybackService.Volume): AppSettings.Set("Volume", Playback.Volume); break;
                case nameof(PlaybackService.IsMuted): AppSettings.Set("Muted", Playback.IsMuted); break;
                case nameof(PlaybackService.IsShuffle): AppSettings.Set("Shuffle", Playback.IsShuffle); break;
                case nameof(PlaybackService.RepeatMode): AppSettings.Set("Repeat", (int)Playback.RepeatMode); break;
                case nameof(PlaybackService.UseExclusiveMode): AppSettings.Set("ExclusiveMode", Playback.UseExclusiveMode); break;
                case nameof(PlaybackService.OutputDeviceId):
                    AppSettings.Set("OutputDeviceId", Playback.OutputDeviceId ?? "");
                    AppSettings.Set("OutputDeviceName", Playback.OutputDeviceName);
                    break;
            }
        };
    }
}

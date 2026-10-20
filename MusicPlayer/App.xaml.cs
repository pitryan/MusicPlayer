using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}

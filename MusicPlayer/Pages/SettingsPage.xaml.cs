using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace MusicPlayer.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DatabasePathText.Text = App.Database.DatabasePath;
        CrashLogPathText.Text = CrashLog.FilePath;
        VersionText.Text = $"Version {AppInfo.Version}";
        RefreshFolders();

        // Baris status hanya tampil kalau ada isinya, supaya kartu Library tidak punya ruang kosong di bawah
        StatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            StatusText.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible);

        // Lapisan lirik juga bisa diubah dari tombol di panel lirik, termasuk selama halaman ini terbuka. Changed adalah
        // event statis, jadi dilepas lagi saat halaman ditutup supaya halamannya tidak tertahan di memori.
        Loaded += (_, _) =>
        {
            SyncLyricsToggles();
            LyricsDisplay.Changed += OnLyricsDisplayChanged;
        };
        Unloaded += (_, _) => LyricsDisplay.Changed -= OnLyricsDisplayChanged;

        // Scan bisa masih berjalan dari halaman Settings sebelumnya, karena halaman ini dibuat baru setiap kali dibuka
        Loaded += (_, _) =>
        {
            LibraryScanState.Changed += OnLibraryScanChanged;
            SyncScanState();
            if (LibraryScanState.IsRunning)
                StatusText.Text = LibraryScanState.Status;
        };
        Unloaded += (_, _) => LibraryScanState.Changed -= OnLibraryScanChanged;
    }

    private void OnLyricsDisplayChanged(object? sender, EventArgs e) => SyncLyricsToggles();

    private void OnLibraryScanChanged(object? sender, EventArgs e)
    {
        SyncScanState();
        StatusText.Text = LibraryScanState.Status;
        if (!LibraryScanState.IsRunning)
            RefreshFolders();
    }

    /// <summary>
    /// Selama scan berjalan, daftar folder tidak boleh berubah: scan sudah membaca daftar itu di awal. Mematikan
    /// FolderList ikut mematikan semua tombol Remove di dalamnya, termasuk yang dibuat ulang oleh RefreshFolders.
    /// </summary>
    private void SyncScanState()
    {
        bool scanning = LibraryScanState.IsRunning;
        AddFolderButton.IsEnabled = !scanning;
        ScanButton.IsEnabled = !scanning;
        FolderList.IsEnabled = !scanning;
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncLyricsToggles()
    {
        ShowReadingToggle.IsOn = LyricsDisplay.ShowReading;
        ShowTranslationToggle.IsOn = LyricsDisplay.ShowTranslation;
    }

    /// <summary>
    /// Digulir ke bagian Playback &amp; Audio, untuk tombol "Audio Quality Settings" di popup kualitas. Halaman baru dibuat
    /// oleh Frame.Navigate, jadi tunggu sampai tampil dulu.
    /// </summary>
    public void ShowAudioSettings()
    {
        if (IsLoaded)
            BringAudioIntoView();
        else
            Loaded += OnLoadedShowAudio;

        void OnLoadedShowAudio(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedShowAudio;
            BringAudioIntoView();
        }
    }

    private void BringAudioIntoView() =>
        AudioHeader.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });

    private void RefreshFolders()
    {
        var folders = App.Database.GetFolders();
        FolderList.ItemsSource = folders;
        NoFoldersPanel.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Toggled juga terpicu saat IsOn diisi dari kode; hanya tulis kalau nilainya berubah agar panel lirik tidak digambar ulang percuma
    private void ShowReadingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (LyricsDisplay.ShowReading != ShowReadingToggle.IsOn)
            LyricsDisplay.ShowReading = ShowReadingToggle.IsOn;
    }

    private void ShowTranslationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (LyricsDisplay.ShowTranslation != ShowTranslationToggle.IsOn)
            LyricsDisplay.ShowTranslation = ShowTranslationToggle.IsOn;
    }

    private async void OpenCrashLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Folder-nya baru dibuat saat error pertama dicatat
            Directory.CreateDirectory(CrashLog.Folder);
            await Launcher.LaunchFolderPathAsync(CrashLog.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder data aplikasi tidak bisa dibuat: path-nya tetap tampil dan bisa disalin
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // Di aplikasi desktop, picker perlu tahu jendela induknya
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        var result = App.Database.AddFolder(folder.Path);
        switch (result.Status)
        {
            case AddFolderStatus.AlreadyCovered:
                StatusText.Text = $"This folder is already part of {result.Folder}";
                return;
            case AddFolderStatus.NotFound:
                StatusText.Text = $"Folder not found: {result.Folder}";
                return;
        }

        RefreshFolders();
        App.MainWindow.RefreshFolderItems();
        App.LibraryWatcher.SyncFolders();
        await LibraryScanState.ScanAsync(App.Database);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string folder)
        {
            int removed = App.Database.RemoveFolder(folder);
            StatusText.Text = $"Removed from library ({removed} songs). Your files were not deleted.";
            RefreshFolders();
            App.MainWindow.RefreshFolderItems();
            App.LibraryWatcher.SyncFolders();
        }
    }

    /// <summary>
    /// Folder musik yang dipindah (misalnya ke drive lain): library diarahkan ke lokasi barunya. Kalau folder lama dihapus
    /// lalu folder baru ditambahkan, riwayat putar, favorit, dan isi playlist lagu-lagunya terputus karena disambungkan
    /// lewat path.
    /// </summary>
    private async void RelocateFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string oldFolder)
            return;

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        StorageFolder? picked = await picker.PickSingleFolderAsync();
        if (picked is null)
            return;

        string newFolder = LibraryPaths.NormalizeFolder(picked.Path);
        if (string.Equals(newFolder, oldFolder, StringComparison.Ordinal))
        {
            StatusText.Text = "That's where this folder already is. Choose the folder's new location.";
            return;
        }

        // Mengecek setiap file, jadi tidak di UI thread
        var preview = await Task.Run(() => App.Database.PreviewRelocation(oldFolder, newFolder));
        if (!await Dialogs.ConfirmAsync("Change Folder Location?", DescribeRelocation(preview, newFolder), "Change Location"))
            return;

        // Scan yang dimulai selama dialog terbuka sudah membaca daftar folder yang lama
        if (LibraryScanState.IsRunning)
        {
            StatusText.Text = "Wait for the library scan to finish, then try again.";
            return;
        }

        var result = App.Database.RelocateFolder(oldFolder, newFolder);
        switch (result.Status)
        {
            case RelocateFolderStatus.NotFound:
                StatusText.Text = $"Folder not found: {result.Folder}";
                return;
            case RelocateFolderStatus.NotInLibrary or RelocateFolderStatus.SameFolder:
                RefreshFolders();
                return;
        }

        App.Playback.RelocateTracks(oldFolder, result.Folder);
        App.Favourites.Reload();
        App.Playlists.NotifyChanged();
        RefreshFolders();
        App.MainWindow.RefreshFolderItems();
        App.LibraryWatcher.SyncFolders();

        // Lagu yang tidak ada di lokasi baru dibuang, dan lagu baru di sana ditambahkan
        await LibraryScanState.ScanAsync(App.Database);
    }

    private static string DescribeRelocation(RelocationPreview preview, string newFolder)
    {
        const string Keeps = "Their play history, favourites, playlists, and quality checks move with them.";
        const string Missing = "Songs that aren't there will be removed from your library when it's rescanned.";

        if (preview.Songs == 0)
            return $"Your library will use \"{newFolder}\" instead. There are no songs from this folder in your library yet.";
        if (preview.Found == 0)
            return $"None of the {Formatters.Count(preview.Songs, "song")} from this folder were found in \"{newFolder}\". " +
                   $"Make sure it's the right folder. {Missing}";
        if (preview.Found == preview.Songs)
            return $"All {Formatters.Count(preview.Songs, "song")} from this folder were found in \"{newFolder}\". {Keeps}";
        return $"{preview.Found} of {Formatters.Count(preview.Songs, "song")} from this folder were found in \"{newFolder}\". " +
               $"{Keeps} {Missing}";
    }

    // Tombol dan kemajuan diatur OnLibraryScanChanged, juga untuk halaman Settings lain yang dibuka selama scan
    private async void Scan_Click(object sender, RoutedEventArgs e) => await LibraryScanState.ScanAsync(App.Database);
}

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MusicPlayer.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DatabasePathText.Text = App.Database.DatabasePath;
        VersionText.Text = $"Version {GetAppVersion()}";
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
    }

    private void OnLyricsDisplayChanged(object? sender, EventArgs e) => SyncLyricsToggles();

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

    private static string GetAppVersion()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception)
        {
            // Package.Current melempar kalau aplikasi berjalan tanpa identitas package (mis. unpackaged saat debug)
            Version? version = typeof(App).Assembly.GetName().Version;
            return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
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
        await ScanAsync();
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string folder)
        {
            int removed = App.Database.RemoveFolder(folder);
            StatusText.Text = $"Removed from library ({removed} songs). Your files were not deleted.";
            RefreshFolders();
            App.MainWindow.RefreshFolderItems();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        AddFolderButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        ScanProgressBar.Visibility = Visibility.Visible;

        // Progress<T> yang dibuat di UI thread otomatis melapor kembali ke UI thread
        var progress = new Progress<ScanProgress>(p =>
            StatusText.Text = p.Total > 0 ? $"{p.Stage}... {p.Done}/{p.Total}" : $"{p.Stage}...");

        try
        {
            var scanner = new LibraryScanner(App.Database);
            var result = await Task.Run(() => scanner.Scan(progress));

            string message =
                $"Done in {result.Elapsed.TotalSeconds:0.0} s: {result.Added} added, {result.Updated} updated, " +
                $"{result.Removed} removed, {result.Unchanged} unchanged.";
            if (result.Errors.Count > 0)
                message += $" {result.Errors.Count} files had unreadable tags.";
            if (result.UnavailableFolders.Count > 0)
                message += $" {result.UnavailableFolders.Count} folders were unavailable and skipped.";
            StatusText.Text = message;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            AddFolderButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
            RefreshFolders();
        }
    }
}

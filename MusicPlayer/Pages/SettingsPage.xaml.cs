using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        RefreshFolders();
    }

    private void RefreshFolders() => FolderList.ItemsSource = App.Database.GetFolders();

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
        await ScanAsync();
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string folder)
        {
            int removed = App.Database.RemoveFolder(folder);
            StatusText.Text = $"Removed from library ({removed} songs). Your files were not deleted.";
            RefreshFolders();
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Controls;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>
/// Library Health: memeriksa folder musik setiap kali halaman dibuka, lalu menampilkan empat bagian yang bisa
/// dibersihkan masing-masing (lihat <see cref="LibraryHealth"/>).
/// </summary>
public sealed partial class LibraryHealthPage : Page
{
    private CancellationTokenSource? _scanCts;
    private IReadOnlyList<string> _folders = [];
    private bool _busy;

    public LibraryHealthPage()
    {
        InitializeComponent();
        PaddingSection.ActionClick += Padding_ActionClick;
        ImagesSection.ActionClick += Images_ActionClick;
        LyricsSection.ActionClick += Lyrics_ActionClick;
        LeftoversSection.ActionClick += Leftovers_ActionClick;
    }

    private IEnumerable<HealthSection> Sections => [PaddingSection, ImagesSection, LyricsSection, LeftoversSection];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ScanAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _scanCts?.Cancel();
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        foreach (var section in Sections)
            section.ShowStatus("");
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        _folders = App.Database.GetFolders();
        SetScanning(true);
        try
        {
            var folders = _folders;
            var report = await Task.Run(() => LibraryHealth.Scan(folders, cts.Token), cts.Token);
            Show(report);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_scanCts == cts)
                SetScanning(false);
        }
    }

    private void SetScanning(bool scanning)
    {
        ScanRing.IsActive = scanning;
        ScanRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanText.Text = scanning ? "Scanning your music folders…" : ScanText.Text;
        ScanButton.IsEnabled = !scanning && !_busy;
    }

    private void Show(HealthReport report)
    {
        PaddingSection.Show(report.Padding.Select(p =>
                new HealthRow(p, Path.GetFileName(p.Path), FolderText(p.Path), Formatters.FileSize(p.Bytes))).ToList(),
            $"{Formatters.Count(report.Padding.Count, "song")} · {Formatters.FileSize(report.Padding.Sum(p => p.Bytes))}",
            "Nothing to clean up");
        ImagesSection.Show(FileRows(report.SpareImages),
            $"{Formatters.Count(report.SpareImages.Count, "image")} · {Formatters.FileSize(report.SpareImages.Sum(i => i.Bytes))}",
            "Nothing to clean up");
        LyricsSection.Show(report.LyricFiles.Select(l => new HealthRow(l,
                l.RenameTo is { } target ? $"{Path.GetFileName(l.Path)}  →  {Path.GetFileName(target)}" : Path.GetFileName(l.Path),
                FolderText(l.Path),
                l.RenameTo is null ? "No matching song" : "Rename")).ToList(),
            Formatters.Count(report.LyricFiles.Count, "file"),
            "Every lyric file matches a song");
        LeftoversSection.Show(FileRows(report.Leftovers),
            $"{Formatters.Count(report.Leftovers.Count, "file")} · {Formatters.FileSize(report.Leftovers.Sum(l => l.Bytes))}",
            "Nothing to clean up");

        bool clean = report.Padding.Count + report.SpareImages.Count + report.LyricFiles.Count + report.Leftovers.Count == 0;
        ScanText.Text = clean ? "Everything looks clean." : "";
    }

    private List<HealthRow> FileRows(IReadOnlyList<SpareFileItem> items) =>
        items.Select(i => new HealthRow(i, Path.GetFileName(i.Path), FolderText(i.Path), Formatters.FileSize(i.Bytes))).ToList();

    /// <summary>Folder file itu, dimulai dari nama folder library-nya: "Japan\LiSA\oath sign".</summary>
    private string FolderText(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        string? root = _folders
            .Select(f => Path.TrimEndingDirectorySeparator(f))
            .Where(f => folder.Equals(f, StringComparison.OrdinalIgnoreCase)
                        || folder.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .MaxBy(f => f.Length);
        if (root is null)
            return folder;
        string relative = Path.GetRelativePath(root, folder);
        return relative == "." ? Path.GetFileName(root) : Path.Combine(Path.GetFileName(root), relative);
    }

    // ── Pembersihan ──────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var section in Sections)
            section.IsBusy = busy;
        ScanButton.IsEnabled = !busy && !ScanRing.IsActive;
    }

    private async void Padding_ActionClick(object? sender, EventArgs e)
    {
        var items = PaddingSection.SelectedRows.Select(r => (PaddingItem)r.Item).ToList();
        string size = Formatters.FileSize(items.Sum(i => i.Bytes));
        if (items.Count == 0 || !await Dialogs.ConfirmAsync("Remove Empty Space?",
                $"{Formatters.Count(items.Count, "FLAC file")} will be rewritten without their empty space, saving {size}. "
                + "The audio, tags, and artwork stay exactly the same: each new copy is compared with the original before it replaces it.",
                "Remove"))
            return;

        SetBusy(true);
        int started = 0;
        IProgress<int> progress = new Progress<int>(n => PaddingSection.ShowStatus($"Cleaning {n} of {items.Count}…"));
        var result = await App.TagEdits.RewriteAsync(items.Select(i => i.Path).ToList(), path =>
        {
            progress.Report(Interlocked.Increment(ref started));
            LibraryHealth.RemovePadding(path);
        });

        var failed = result.Failed.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cleaned = items.Where(i => !failed.Contains(i.Path)).ToList();
        string status = cleaned.Count > 0
            ? $"Removed {Formatters.FileSize(cleaned.Sum(i => i.Bytes))} from {Formatters.Count(cleaned.Count, "song")}."
            : "";
        await FinishAsync(PaddingSection, status, result.Failed);
    }

    private async void Images_ActionClick(object? sender, EventArgs e)
    {
        var items = ImagesSection.SelectedRows.Select(r => (SpareFileItem)r.Item).ToList();
        if (items.Count == 0 || !await Dialogs.ConfirmAsync("Move to Recycle Bin?",
                $"{Formatters.Count(items.Count, "image")} ({Formatters.FileSize(items.Sum(i => i.Bytes))}) will be moved to the Recycle Bin. "
                + "Your songs keep their embedded artwork. You can restore the images from the Recycle Bin.",
                "Move"))
            return;

        SetBusy(true);
        var failed = await Task.Run(() => Each(items, i => i.Path, i => RecycleBin.Send(i.Path)));
        await FinishAsync(ImagesSection, Moved(items.Count - failed.Count, "image"), failed);
    }

    private async void Lyrics_ActionClick(object? sender, EventArgs e)
    {
        var items = LyricsSection.SelectedRows.Select(r => (LyricFileItem)r.Item).ToList();
        int renames = items.Count(i => i.RenameTo is not null);
        int orphans = items.Count - renames;
        var parts = new List<string>();
        if (renames > 0)
            parts.Add($"{Formatters.Count(renames, "lyric file")} will be renamed to match their songs, so their lyrics show up.");
        if (orphans > 0)
            parts.Add($"{Formatters.Count(orphans, "lyric file")} without a song will be moved to the Recycle Bin.");
        if (items.Count == 0 || !await Dialogs.ConfirmAsync("Fix Lyric Files?", string.Join(" ", parts), "Fix"))
            return;

        SetBusy(true);
        var failed = await Task.Run(() => Each(items, i => i.Path, i => LibraryHealth.FixLyricFile(i, RecycleBin.Send)));

        // Lirik lagu yang sedang diputar mungkin baru saja tersambung
        if (App.Playback.CurrentTrack is { } playing
            && items.Any(i => i.RenameTo is { } target
                              && string.Equals(target, Path.ChangeExtension(playing.FilePath, ".lrc"), StringComparison.OrdinalIgnoreCase)))
            App.Lyrics.Retry();

        var done = items.Where(i => failed.All(f => !string.Equals(f.Path, i.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        var status = new List<string>();
        if (done.Count(i => i.RenameTo is not null) is > 0 and var renamed)
            status.Add($"Renamed {Formatters.Count(renamed, "lyric file")}.");
        if (done.Count(i => i.RenameTo is null) is > 0 and var moved)
            status.Add(Moved(moved, "lyric file"));
        await FinishAsync(LyricsSection, string.Join(" ", status), failed);
    }

    private async void Leftovers_ActionClick(object? sender, EventArgs e)
    {
        var items = LeftoversSection.SelectedRows.Select(r => (SpareFileItem)r.Item).ToList();
        if (items.Count == 0 || !await Dialogs.ConfirmAsync("Move to Recycle Bin?",
                $"{Formatters.Count(items.Count, "file")} ({Formatters.FileSize(items.Sum(i => i.Bytes))}) will be moved to the Recycle Bin.",
                "Move"))
            return;

        SetBusy(true);
        var failed = await Task.Run(() => Each(items, i => i.Path, i => RecycleBin.Send(i.Path)));
        await FinishAsync(LeftoversSection, Moved(items.Count - failed.Count, "file"), failed);
    }

    private static string Moved(int count, string noun) =>
        count > 0 ? $"Moved {Formatters.Count(count, noun)} to the Recycle Bin." : "";

    /// <summary>Satu file yang gagal tidak menghentikan file lainnya.</summary>
    private static List<(string Path, string Error)> Each<T>(IEnumerable<T> items, Func<T, string> pathOf, Action<T> act)
    {
        var failed = new List<(string Path, string Error)>();
        foreach (var item in items)
        {
            try
            {
                act(item);
            }
            catch (Exception ex)
            {
                failed.Add((pathOf(item), ex.Message));
            }
        }
        return failed;
    }

    /// <summary>Hasilnya ditulis di bagian itu, lalu folder musik diperiksa ulang.</summary>
    private async Task FinishAsync(HealthSection section, string done, IReadOnlyList<(string Path, string Error)> failed)
    {
        string status = done;
        if (failed.Count > 0)
            status = $"{done} Couldn't clean {Formatters.Count(failed.Count, "file")}: "
                     + string.Join("; ", failed.Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})"));
        SetBusy(false);
        await ScanAsync();
        section.ShowStatus(status.Trim());
    }
}

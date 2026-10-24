using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.ViewModels;
using Windows.Foundation;

namespace MusicPlayer.Pages;

/// <summary>Hasil pemeriksaan kualitas sumber seluruh library, dan tombol untuk memeriksa yang belum.</summary>
public sealed partial class QualityPage : Page
{
    private readonly ObservableCollection<QualityRow> _rows = [];
    private Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, QualityReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    // SelectorBar sudah memicu SelectionChanged saat XAML-nya dibangun, sebelum halaman siap
    private bool _ready;

    // Lagu lossless yang belum diperiksa (dihitung ShowSummary)
    private int _pending;

    public QualityPage()
    {
        InitializeComponent();
        QualityList.ItemsSource = _rows;
    }

    private bool ShowAll => FilterTabs.SelectedItem == AllTab;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        Load();
        App.QualityChecks.Checked += QualityChecks_Checked;
        App.QualityChecks.ScanProgressChanged += QualityChecks_ScanProgressChanged;
        ShowScanState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.QualityChecks.Checked -= QualityChecks_Checked;
        App.QualityChecks.ScanProgressChanged -= QualityChecks_ScanProgressChanged;
    }

    private void Load()
    {
        _tracks = App.Database.GetTracks().ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
        _reports = new Dictionary<string, QualityReport>(App.Database.GetQualityReports(), StringComparer.OrdinalIgnoreCase);

        _rows.Clear();
        // Urutan library (artis album, album, disc, nomor track), seperti halaman Songs
        foreach (var track in _tracks.Values)
        {
            if (_reports.TryGetValue(track.FilePath, out var report) && Matches(track, report))
                _rows.Add(new QualityRow(track, report));
        }
        DetailPanel.Visibility = Visibility.Collapsed;
        ShowSummary();
    }

    private bool Matches(Track track, QualityReport report) => ShowAll || Formatters.IsSuspicious(report, track);

    /// <summary>Contoh: "640 songs checked · 12 upsampled · 3 likely from lossy · 1 with unused bits · 85 not checked yet".</summary>
    private void ShowSummary()
    {
        var checkedPairs = _reports
            .Where(r => _tracks.ContainsKey(r.Key))
            .Select(r => (Track: _tracks[r.Key], Report: r.Value))
            .Where(p => AudioQuality.Describe(p.Track).IsLossless)
            .ToList();
        int upsampled = checkedPairs.Count(p => p.Report.Verdict == SourceVerdict.Upsampled);
        int lossy = checkedPairs.Count(p => p.Report.Verdict is SourceVerdict.LikelyLossy or SourceVerdict.LimitedBandwidth);
        int padded = checkedPairs.Count(p => p.Track.BitsPerSample >= 24 && !p.Report.IsFloat && p.Report.EffectiveBits is <= 16);
        int pending = _tracks.Values.Count(t => AudioQuality.Describe(t).IsLossless && !_reports.ContainsKey(t.FilePath));
        _pending = pending;

        var parts = new List<string> { $"{Formatters.Count(checkedPairs.Count, "song")} checked" };
        if (upsampled > 0)
            parts.Add($"{upsampled} upsampled");
        if (lossy > 0)
            parts.Add($"{lossy} likely from lossy");
        if (padded > 0)
            parts.Add($"{padded} with unused bits");
        if (pending > 0)
            parts.Add($"{pending} not checked yet");
        SummaryText.Text = string.Join(" · ", parts);

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = checkedPairs.Count == 0
            ? "No songs have been checked yet. Click Check Library to check your lossless songs in the background."
            : ShowAll ? "No checked songs." : "Nothing suspicious so far.";
    }

    private void ShowScanState()
    {
        var checks = App.QualityChecks;
        bool scanning = checks.IsScanning;
        ScanRing.IsActive = scanning;
        ScanRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.Content = scanning ? "Stop" : "Check Library";
        ScanText.Text = scanning
            ? $"Checking {Math.Min(checks.ScanDone + 1, checks.ScanTotal)} of {checks.ScanTotal}…"
            : _pending == 0 && _reports.Count > 0 ? "All lossless songs are checked." : "";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (App.QualityChecks.IsScanning)
        {
            App.QualityChecks.StopScan();
            return;
        }
        await App.QualityChecks.ScanLibraryAsync();
    }

    private void QualityChecks_ScanProgressChanged(object? sender, EventArgs e) => ShowScanState();

    /// <summary>Satu lagu selesai diperiksa (dari sini atau dari popup badge): barisnya ditambah atau diperbarui.</summary>
    private void QualityChecks_Checked(object? sender, Track track)
    {
        if (App.QualityChecks.Cached(track) is not { } report)
            return;

        _tracks[track.FilePath] = track;
        _reports[track.FilePath] = report with { Spectrum = [] };

        int index = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Track.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index >= 0)
            _rows.RemoveAt(index);
        if (Matches(track, report))
            _rows.Insert(index >= 0 ? index : _rows.Count, new QualityRow(track, report));
        ShowSummary();
    }

    private void FilterTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_ready)
            Load();
    }

    private void QualityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityList.SelectedItem is not QualityRow row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        // Daftar hanya memuat ringkasan; spektrum lengkap diambil dari database saat dipilih
        var report = App.QualityChecks.Cached(row.Track) ?? row.Report;
        DetailPanel.Visibility = Visibility.Visible;
        DetailTitleText.Text = $"{row.Title} — {row.Track.DisplayArtist}";
        DetailFormatText.Text = row.FormatText;
        DetailVerdictText.Text = row.VerdictText;
        DetailText.Text = Formatters.SourceDetailText(report, row.Track);
        DetailChart.Show(report);
    }

    private void QualityList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is QualityRow row)
            row.EnsureCover();
    }

    private void QualityList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan daftar ini sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QualityRow row)
            App.Playback.PlayQueue(_rows.Select(r => r.Track).ToList(), _rows.IndexOf(row));
    }

    private void QualityList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QualityRow row)
            TrackMenu.Show(QualityList, e.GetPosition(QualityList), _rows.Select(r => r.Track).ToList(), _rows.IndexOf(row));
    }
}

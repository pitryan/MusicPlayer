using System;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Foundation;
using Windows.Graphics;

namespace MusicPlayer;

/// <summary>
/// Jendela kecil yang selalu di atas jendela lain. Lebarnya tetap; tombol lirik dan antrean memanjangkannya ke bawah
/// untuk menampilkan panelnya. Posisi dan panel yang terbuka diingat untuk lain kali. Tombol × menutupnya dan
/// memunculkan jendela utama lagi; tombol di sebelahnya pindah ke layar penuh (keduanya diatur MainWindow).
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    // Ukuran dalam satuan tampilan (DIP); dikali skala layar saat jendela dibuat
    private const double WidthDip = 380;
    private const double HeightDip = 172;

    // Tinggi tambahan saat panel lirik atau antrean terbuka
    private const double PanelHeightDip = 340;

    private const string PanelSetting = "MiniPlayerPanel";
    private const string LyricsPanel = "lyrics";
    private const string QueuePanel = "queue";

    // Panel yang terbuka: "", "lyrics", atau "queue"
    private string _panel = "";

    private PointInt32 _position;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        if (Content is UIElement root)
            PlaybackShortcuts.Attach(root); // Spasi, Ctrl+←/→, Ctrl+↑/↓

        string savedPanel = AppSettings.Get(PanelSetting, "");
        _panel = savedPanel is LyricsPanel or QueuePanel ? savedPanel : "";
        ShowPanelContent();
        Playback.UpNext.CollectionChanged += UpNext_CollectionChanged;
        Playback.PropertyChanged += Playback_PropertyChanged;

        // Selalu di atas, ukuran tetap, tanpa minimize/maximize: di pojok kanan atas hanya tersisa tombol ×
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(InfoArea);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        PlaceWindow();

        // Posisi dicatat setiap kali jendela dipindah, lalu disimpan saat ditutup. AppWindow tidak dibaca lagi
        // di event Closed, karena saat itu jendelanya sedang dibongkar.
        _position = AppWindow.Position;
        AppWindow.Changed += (sender, args) =>
        {
            if (args.DidPositionChange)
                _position = sender.Position;
        };
        Closed += (_, _) =>
        {
            AppSettings.Set("MiniPlayerX", _position.X);
            AppSettings.Set("MiniPlayerY", _position.Y);
            Playback.UpNext.CollectionChanged -= UpNext_CollectionChanged;
            Playback.PropertyChanged -= Playback_PropertyChanged;
            MiniLyrics.IsActive = false;
        };
    }

    public PlaybackService Playback { get; } = App.Playback;

    /// <summary>
    /// Posisi terakhir dipakai lagi kalau masih ada di salah satu layar (layar kedua bisa saja sudah dicabut).
    /// Kalau belum pernah dibuka, MiniPlayer muncul di pojok kanan bawah layar tempat jendela utama berada.
    /// </summary>
    private void PlaceWindow()
    {
        double scale = App.MainWindow.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        int width = (int)Math.Round(WidthDip * scale);
        int height = (int)Math.Round(CurrentHeightDip * scale);

        int x = AppSettings.Get("MiniPlayerX", int.MinValue);
        int y = AppSettings.Get("MiniPlayerY", int.MinValue);
        // Dicek dari bagian atas jendela, karena panel yang terbuka membuatnya lebih tinggi dari saat posisinya disimpan
        if (x != int.MinValue && y != int.MinValue
            && DisplayArea.GetFromPoint(new PointInt32(x + width / 2, y + 20), DisplayAreaFallback.None) is { } area)
        {
            AppWindow.MoveAndResize(new RectInt32(x, WindowFit.Top(y, height, area.WorkArea.Y, area.WorkArea.Height), width, height));
            return;
        }

        RectInt32 work = DisplayArea.GetFromWindowId(App.MainWindow.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int margin = (int)Math.Round(24 * scale);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + work.Width - width - margin,
            work.Y + work.Height - height - margin,
            width,
            height));
    }

    private double CurrentHeightDip => HeightDip + (_panel.Length > 0 ? PanelHeightDip : 0);

    // ── Panel lirik dan antrean ─────────────────────────────

    /// <summary>Tombol lirik atau antrean: membuka panelnya, atau menutupnya kalau panel itu yang sedang terbuka.</summary>
    private void PanelToggle_Click(object sender, RoutedEventArgs e)
    {
        string clicked = sender == LyricsToggle ? LyricsPanel : QueuePanel;
        _panel = _panel == clicked ? "" : clicked;
        AppSettings.Set(PanelSetting, _panel);
        ShowPanelContent();
        ResizeForPanel();
        if (_panel.Length > 0)
            Animations.SlideIn(PanelArea, 0, -16);
    }

    // Cover dan judul memudar masuk setiap kali lagu berganti
    private void Playback_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack) && Playback.CurrentTrack is not null)
            Animations.FadeIn(InfoArea);
    }

    private void ShowPanelContent()
    {
        LyricsToggle.IsChecked = _panel == LyricsPanel;
        QueueToggle.IsChecked = _panel == QueuePanel;
        PanelArea.Visibility = _panel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MiniLyrics.Visibility = _panel == LyricsPanel ? Visibility.Visible : Visibility.Collapsed;
        // Lirik hanya dicari selama panelnya terlihat, seperti di jendela utama
        MiniLyrics.IsActive = _panel == LyricsPanel;
        MiniQueue.Visibility = _panel == QueuePanel ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueueEmpty();
    }

    /// <summary>Memanjang (atau memendek) ke bawah; kalau tidak muat di layar, jendela naik secukupnya.</summary>
    private void ResizeForPanel()
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        int width = (int)Math.Round(WidthDip * scale);
        int height = (int)Math.Round(CurrentHeightDip * scale);
        var position = AppWindow.Position;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(position.X, WindowFit.Top(position.Y, height, work.Y, work.Height), width, height));
    }

    private void UpNext_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateQueueEmpty();

    private void UpdateQueueEmpty() =>
        MiniQueueEmpty.Visibility = Playback.UpNext.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void MiniQueueList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is QueueEntry entry)
            entry.EnsureCover();
    }

    private void MiniQueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueEntry entry)
            Playback.PlayFromQueue(entry);
    }

    // Menu yang sama dengan antrean di jendela utama. Go to Album/Artist dan Get Info menutup MiniPlayer dan
    // membuka halamannya di jendela utama (lihat MainWindow.ShowPage).
    private void MiniQueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueEntry entry)
            TrackMenu.ShowForQueueEntry(MiniQueueList, e.GetPosition(MiniQueueList), entry);
    }

    // ── Title bar ────────────────────────────────────────────

    private void TitleBarArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarRegions();

    /// <summary>
    /// Baris atas berfungsi sebagai title bar, kecuali badge kualitas dan tombol layar penuh yang harus tetap
    /// bisa diklik. Dihitung ulang saat ukurannya berubah, termasuk saat badge baru muncul begitu lagu diputar.
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (Content?.XamlRoot is not { IsHostVisible: true } xamlRoot)
            return;

        double scale = xamlRoot.RasterizationScale;

        // Sisakan ruang untuk tombol × milik Windows di kanan atas
        double captionWidth = AppWindow.TitleBar.RightInset / scale;
        if (double.IsFinite(captionWidth) && captionWidth >= 0)
            CaptionColumn.Width = new GridLength(captionWidth);

        RectInt32[] interactive =
        [
            ToWindowRect(Badge, scale),
            ToWindowRect(FullScreenButton, scale),
        ];
        InputNonClientPointerSource
            .GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, interactive);
    }

    /// <summary>Posisi elemen di jendela dalam piksel fisik layar, sama seperti di MainWindow.</summary>
    private static RectInt32 ToWindowRect(FrameworkElement element, double scale)
    {
        Rect bounds = element.TransformToVisual(null)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(Math.Max(0, bounds.Width) * scale),
            (int)Math.Round(Math.Max(0, bounds.Height) * scale));
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => App.MainWindow.ShowFullScreen();

    private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Klik di garis progres: lompat ke posisi itu
        var bar = (FrameworkElement)sender;
        if (bar.ActualWidth > 0)
            Playback.SeekToFraction(e.GetCurrentPoint(bar).Position.X / bar.ActualWidth);
    }
}

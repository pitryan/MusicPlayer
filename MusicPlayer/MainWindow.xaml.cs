using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Pages;
using MusicPlayer.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace MusicPlayer;

public sealed partial class MainWindow : Window
{
    /// <summary>Dipakai oleh x:Bind di MainWindow.xaml.</summary>
    public PlaybackService Playback { get; } = App.Playback;

    public MainWindow()
    {
        InitializeComponent();

        // Bar atas menyatu dengan title bar, seperti Apple Music
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TopBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.Resize(new SizeInt32(1400, 900));

        // Hitung ulang area yang bisa diklik setiap kali ukuran LCD berubah atau skala layar diganti
        LcdBorder.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TopBar.Loaded += (_, _) =>
        {
            if (Content?.XamlRoot is { } xamlRoot)
                xamlRoot.Changed += (_, _) => UpdateTitleBarRegions();
        };

        // Tombol back hanya aktif kalau ada halaman untuk kembali, misalnya dari detail album ke Albums
        ContentFrame.Navigated += (_, _) => NavView.IsBackEnabled = ContentFrame.CanGoBack;

        // Halaman pertama: Recently Added
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();

        Closed += (_, _) => Playback.Dispose();
    }

    // ── Bar atas ─────────────────────────────────────────────

    private void TopBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarRegions();

    /// <summary>
    /// Seluruh bar atas berfungsi sebagai title bar (bisa diseret untuk memindah jendela), kecuali
    /// tombol transport dan LCD yang harus tetap bisa diklik. Area itu ditandai sebagai "passthrough".
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (Content?.XamlRoot is not { } xamlRoot)
            return;

        double scale = xamlRoot.RasterizationScale; // misalnya 1.25 kalau skala layar 125%

        // Sisakan ruang untuk tombol minimize/maximize/close milik Windows di kanan
        CaptionButtonsColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        RectInt32[] interactive = [ToWindowRect(TransportPanel, scale), ToWindowRect(LcdBorder, scale)];
        InputNonClientPointerSource
            .GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, interactive);
    }

    /// <summary>Posisi elemen di jendela, dalam piksel fisik layar.</summary>
    private static RectInt32 ToWindowRect(FrameworkElement element, double scale)
    {
        Rect bounds = element.TransformToVisual(null)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Playback.Previous();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => Playback.TogglePlayPause();

    private void Next_Click(object sender, RoutedEventArgs e) => Playback.Next();

    private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Klik di garis progres: lompat ke posisi itu
        var bar = (FrameworkElement)sender;
        if (bar.ActualWidth > 0)
            Playback.SeekToFraction(e.GetCurrentPoint(bar).Position.X / bar.ActualWidth);
    }

    private void AudioSettings_Click(object sender, RoutedEventArgs e)
    {
        QualityFlyout.Hide();
        NavView.SelectedItem = NavView.SettingsItem;
    }

    // ── Navigasi ─────────────────────────────────────────────

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "songs";

        switch (tag)
        {
            case "recent":
                ContentFrame.Navigate(typeof(AlbumsPage), "recent");
                break;
            case "albums":
                ContentFrame.Navigate(typeof(AlbumsPage), "albums");
                break;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                break;
            default:
                ContentFrame.Navigate(typeof(SongsPage));
                break;
        }

        // Pindah lewat sidebar memulai "riwayat" baru, jadi tombol back tidak lompat antar menu
        ContentFrame.BackStack.Clear();
        NavView.IsBackEnabled = false;
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }
}

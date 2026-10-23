using System;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace MusicPlayer;

/// <summary>
/// Jendela kecil yang selalu di atas jendela lain. Ukurannya tetap; posisinya diingat untuk lain kali.
/// Tombol × menutupnya dan memunculkan jendela utama lagi; tombol di sebelahnya pindah ke layar penuh
/// (keduanya diatur MainWindow).
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    // Ukuran dalam satuan tampilan (DIP); dikali skala layar saat jendela dibuat
    private const double WidthDip = 380;
    private const double HeightDip = 172;

    private PointInt32 _position;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        if (Content is UIElement root)
            PlaybackShortcuts.Attach(root); // Spasi, Ctrl+←/→, Ctrl+↑/↓

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
        int height = (int)Math.Round(HeightDip * scale);

        int x = AppSettings.Get("MiniPlayerX", int.MinValue);
        int y = AppSettings.Get("MiniPlayerY", int.MinValue);
        if (x != int.MinValue && y != int.MinValue
            && DisplayArea.GetFromPoint(new PointInt32(x + width / 2, y + height / 2), DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
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

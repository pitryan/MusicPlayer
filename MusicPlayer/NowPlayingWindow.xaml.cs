using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Services;

namespace MusicPlayer;

/// <summary>
/// Pemutar layar penuh, dibuka dari MiniPlayer. Tombol keluar dan Esc kembali ke MiniPlayer; tombol × menutupnya
/// dan memunculkan jendela utama lagi (keduanya diatur MainWindow).
/// </summary>
public sealed partial class NowPlayingWindow : Window
{
    // CurrentCover milik PlaybackService hanya selebar 104 piksel (cukup untuk LCD dan MiniPlayer), jadi terlihat
    // buram kalau diperbesar ke 640 piksel. Layar penuh memuat cover-nya sendiri dengan ukuran ini.
    private const int LargeCoverPixels = 1280;

    public NowPlayingWindow(WindowId displayOf)
    {
        InitializeComponent();

        // Pindah dulu ke layar asal (tempat MiniPlayer berada), baru dijadikan layar penuh: mode layar penuh
        // selalu memakai layar tempat jendela itu sedang berada, dan jendela baru belum tentu muncul di sana
        var display = DisplayArea.GetFromWindowId(displayOf, DisplayAreaFallback.Nearest);
        AppWindow.MoveAndResize(display.OuterBounds);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        Closed += (_, _) => Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        _ = LoadLargeCoverAsync();
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
            _ = LoadLargeCoverAsync();
    }

    private async Task LoadLargeCoverAsync()
    {
        var track = Playback.CurrentTrack;

        // Cover kecil tampil dulu, supaya tidak sempat kosong selama cover besar dimuat
        CoverBrush.ImageSource = Playback.CurrentCover;
        if (track is null)
            return;

        var cover = await CoverImageLoader.LoadAsync(track.FilePath, decodePixelWidth: LargeCoverPixels);

        // Lewati kalau sementara itu lagu sudah berganti
        if (ReferenceEquals(track, Playback.CurrentTrack) && cover is not null)
            CoverBrush.ImageSource = cover;
    }

    private void ExitFullScreen_Click(object sender, RoutedEventArgs e) => App.MainWindow.ShowMiniPlayer();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        App.MainWindow.ShowMiniPlayer();
    }

    private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Klik di garis progres: lompat ke posisi itu
        var bar = (FrameworkElement)sender;
        if (bar.ActualWidth > 0)
            Playback.SeekToFraction(e.GetCurrentPoint(bar).Position.X / bar.ActualWidth);
    }
}

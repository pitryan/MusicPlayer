using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Helpers;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer;

/// <summary>
/// Pemutar layar penuh, dibuka dari MiniPlayer. Tombol keluar dan Esc kembali ke MiniPlayer; tombol × menutupnya
/// dan memunculkan jendela utama lagi (keduanya diatur MainWindow). Tombol lirik memindah pemutar ke kiri dan
/// menampilkan lirik di kanan; pilihan itu diingat untuk lain kali.
/// </summary>
public sealed partial class NowPlayingWindow : Window
{
    // CurrentCover milik PlaybackService hanya selebar 104 piksel (cukup untuk LCD dan MiniPlayer), jadi terlihat
    // buram kalau diperbesar ke 640 piksel. Layar penuh memuat cover-nya sendiri dengan ukuran ini.
    private const int LargeCoverPixels = 1280;

    // Lirik di layar penuh menyala atau tidak, diingat seperti di Apple Music
    private const string ShowLyricsSetting = "FullScreenLyrics";

    // Antrean di layar penuh menyala atau tidak, diingat juga
    private const string ShowQueueSetting = "FullScreenQueue";

    // Lirik sedang tampil di kanan (dipertahankan selama lirik lagu berikutnya dicari)
    private bool _sideBySide;

    public NowPlayingWindow(WindowId displayOf)
    {
        InitializeComponent();
        if (Content is UIElement root)
            PlaybackShortcuts.Attach(root); // Spasi, Ctrl+←/→, Ctrl+↑/↓

        // Pindah dulu ke layar asal (tempat MiniPlayer berada), baru dijadikan layar penuh: mode layar penuh
        // selalu memakai layar tempat jendela itu sedang berada, dan jendela baru belum tentu muncul di sana
        var display = DisplayArea.GetFromWindowId(displayOf, DisplayAreaFallback.Nearest);
        AppWindow.MoveAndResize(display.OuterBounds);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        App.Lyrics.Changed += OnLyricsChanged;
        Playback.UpNext.CollectionChanged += UpNext_CollectionChanged;
        Closed += (_, _) =>
        {
            Playback.PropertyChanged -= OnPlaybackPropertyChanged;
            App.Lyrics.Changed -= OnLyricsChanged;
            Playback.UpNext.CollectionChanged -= UpNext_CollectionChanged;
            LyricsArea.IsActive = false; // berhenti mencari lirik untuk layar ini
        };
        _ = LoadLargeCoverAsync();

        // Antrean dan lirik bergantian; kalau dua-duanya tersimpan menyala, antrean yang dipakai
        QueueToggle.IsChecked = AppSettings.Get(ShowQueueSetting, false);
        LyricsToggle.IsChecked = QueueToggle.IsChecked != true && AppSettings.Get(ShowLyricsSetting, true);
        UpdateLyricsLayout();
    }

    public PlaybackService Playback { get; } = App.Playback;

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
        {
            _ = LoadLargeCoverAsync();
            if (Playback.CurrentTrack is not null)
            {
                // Latar dan cover lagu baru memudar masuk
                Animations.FadeIn(BackdropLayer, 450);
                Animations.FadeIn(CoverFrame);
            }
        }
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

    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (LyricsToggle.IsChecked == true)
            QueueToggle.IsChecked = false;
        SaveSidePanel();
        UpdateLyricsLayout();
    }

    private void QueueToggle_Click(object sender, RoutedEventArgs e)
    {
        if (QueueToggle.IsChecked == true)
            LyricsToggle.IsChecked = false;
        SaveSidePanel();
        UpdateLyricsLayout();
    }

    private void SaveSidePanel()
    {
        AppSettings.Set(ShowLyricsSetting, LyricsToggle.IsChecked == true);
        AppSettings.Set(ShowQueueSetting, QueueToggle.IsChecked == true);
    }

    private void OnLyricsChanged(object? sender, EventArgs e) => UpdateLyricsLayout();

    /// <summary>
    /// Pemutar di kiri dan lirik di kanan kalau lirik dinyalakan dan lagu ini punya lirik; selain itu pemutar di
    /// tengah. Selama lirik lagu berikutnya dicari, tata letak dibiarkan dulu, supaya tidak berpindah dua kali.
    /// </summary>
    private void UpdateLyricsLayout()
    {
        bool queue = QueueToggle.IsChecked == true;
        bool wanted = LyricsToggle.IsChecked == true && !queue;
        LyricsArea.IsActive = wanted;

        var state = App.Lyrics.State;
        if (!wanted)
            _sideBySide = false;
        else if (state != LyricsState.Loading)
            _sideBySide = state == LyricsState.Ready;

        // Pemutar pindah ke kiri kalau kolom kanan dipakai lirik atau antrean
        Grid.SetColumnSpan(PlayerArea, _sideBySide || queue ? 1 : 2);
        LyricsArea.Visibility = _sideBySide ? Visibility.Visible : Visibility.Collapsed;
        if (queue && QueueArea.Visibility != Visibility.Visible)
            Animations.SlideIn(QueueArea, 48, 0);
        QueueArea.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueueEmpty();

        // Lagu tanpa lirik: tombolnya tidak bisa diklik, seperti Apple Music. Pilihannya tetap diingat, jadi lirik
        // muncul lagi begitu lagu berikutnya punya lirik.
        LyricsToggle.IsEnabled = !wanted || state is LyricsState.Ready or LyricsState.Loading;
    }

    private void UpNext_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateQueueEmpty();

    private void UpdateQueueEmpty() =>
        FullQueueEmpty.Visibility = Playback.UpNext.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void FullQueueList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is QueueEntry entry)
            entry.EnsureCover();
    }

    private void FullQueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueEntry entry)
            Playback.PlayFromQueue(entry);
    }

    // Go to Album/Artist dan Get Info menutup layar penuh dan membuka halamannya di jendela utama
    private void FullQueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueEntry entry)
            TrackMenu.ShowForQueueEntry(FullQueueList, e.GetPosition(FullQueueList), entry);
    }

    private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Klik di garis progres: lompat ke posisi itu
        var bar = (FrameworkElement)sender;
        if (bar.ActualWidth > 0)
            Playback.SeekToFraction(e.GetCurrentPoint(bar).Position.X / bar.ActualWidth);
    }
}

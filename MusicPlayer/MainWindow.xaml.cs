using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Library;
using MusicPlayer.Pages;
using MusicPlayer.Helpers;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Foundation;
using Windows.Graphics;

namespace MusicPlayer;

public sealed partial class MainWindow : Window
{
    /// <summary>Dipakai oleh x:Bind di MainWindow.xaml.</summary>
    public PlaybackService Playback { get; } = App.Playback;

    // Pencarian dijalankan sebentar setelah pengguna berhenti mengetik, bukan di setiap huruf
    private readonly DispatcherQueueTimer _searchTimer;

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

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ShowSearch(SearchBox.Text);

        RefreshFolderItems();

        Playback.UpNext.CollectionChanged += (_, _) => UpdateQueueTexts();
        Playback.PlayRecorded += (_, _) => { if (IsHistoryTabSelected) LoadHistory(); };
        UpdateQueueTexts();

        // Halaman pertama: Recently Added
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
        NavigateToSection("recent");

        Closed += (_, _) =>
        {
            // Aplikasi ditutup: MiniPlayer atau layar penuh ikut ditutup, supaya tidak ada jendela yang tertinggal
            _isClosing = true;
            _replacement?.Close();
            Playback.Dispose();
        };
    }

    // ── Bar atas ─────────────────────────────────────────────

    private void TopBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarRegions();

    /// <summary>
    /// Seluruh bar atas berfungsi sebagai title bar (bisa diseret untuk memindah jendela), kecuali
    /// tombol transport dan LCD yang harus tetap bisa diklik. Area itu ditandai sebagai "passthrough".
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        // Saat jendela di-minimize, ukurannya tidak berarti (bahkan bisa negatif), jadi lewati saja.
        // Perhitungan diulang otomatis saat jendela dibuka kembali.
        if (Content?.XamlRoot is not { IsHostVisible: true } xamlRoot
            || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return;

        double scale = xamlRoot.RasterizationScale; // misalnya 1.25 kalau skala layar 125%

        // Sisakan ruang untuk tombol minimize/maximize/close milik Windows di kanan
        double captionWidth = AppWindow.TitleBar.RightInset / scale;
        if (double.IsFinite(captionWidth) && captionWidth >= 0)
            CaptionButtonsColumn.Width = new GridLength(captionWidth);

        RectInt32[] interactive =
        [
            ToWindowRect(TransportPanel, scale),
            ToWindowRect(LcdBorder, scale),
            ToWindowRect(RightButtonsPanel, scale),
        ];
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
            (int)Math.Round(Math.Max(0, bounds.Width) * scale),
            (int)Math.Round(Math.Max(0, bounds.Height) * scale));
    }




    private void ShuffleToggle_Click(object sender, RoutedEventArgs e) =>
        Playback.IsShuffle = ShuffleToggle.IsChecked == true;

    private void RepeatToggle_Click(object sender, RoutedEventArgs e)
    {
        Playback.CycleRepeat();

        // Tombol ini punya tiga keadaan (Off, All, One), jadi status menyalanya diatur sendiri:
        // dari All ke One tombol tetap menyala, walaupun ToggleButton biasanya bergantian nyala-mati.
        RepeatToggle.IsChecked = Playback.IsRepeatOn;
    }

    private void SeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Klik di garis progres: lompat ke posisi itu
        var bar = (FrameworkElement)sender;
        if (bar.ActualWidth > 0)
            Playback.SeekToFraction(e.GetCurrentPoint(bar).Position.X / bar.ActualWidth);
    }

    // ── Panel Playing Next ───────────────────────────────────

    private const int HistoryLimit = 100;

    private bool IsHistoryTabSelected => QueueTabs.SelectedItem == HistoryTab;

    private void QueueToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = QueueToggle.IsChecked == true;
        QueuePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open && IsHistoryTabSelected)
            LoadHistory();
    }

    private void QueueTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool history = IsHistoryTabSelected;
        UpNextList.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        ClearQueueButton.Visibility = history ? Visibility.Collapsed : Visibility.Visible;

        if (history)
            LoadHistory();
        else
            UpdateQueueTexts();
    }

    private void UpdateQueueTexts()
    {
        if (IsHistoryTabSelected)
            return;

        int count = Playback.UpNext.Count;
        QueueCountText.Text = Formatters.Count(count, "song");
        QueueEmptyText.Text = "Nothing up next. Right-click a song and choose Play Next or Add to Queue.";
        QueueEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadHistory()
    {
        var plays = App.Database.GetRecentPlays(HistoryLimit);
        HistoryList.ItemsSource = plays.Select(p => new HistoryEntry(p)).ToList();

        QueueCountText.Text = plays.Count >= HistoryLimit ? $"Last {HistoryLimit} plays" : Formatters.Count(plays.Count, "play");
        QueueEmptyText.Text = "Songs you listen to appear here.";
        QueueEmptyText.Visibility = plays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistoryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is HistoryEntry entry)
            entry.EnsureCover();
    }

    private void HistoryList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Putar lagu itu sekarang juga, tanpa mengubah Playing Next
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HistoryEntry entry)
            Playback.PlayNow(entry.Track);
    }

    private void HistoryList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not HistoryEntry entry)
            return;

        var menu = new MenuFlyout();
        menu.Items.Add(TrackMenu.CreateItem("Play Now", "\uE768", () => Playback.PlayNow(entry.Track)));
        menu.Items.Add(TrackMenu.CreateItem("Play Next", "\uE893", () => Playback.PlayNext(entry.Track)));
        menu.Items.Add(TrackMenu.CreateItem("Add to Queue", "\uE710", () => Playback.AddToQueue(entry.Track)));
        TrackMenu.AddGoToItems(menu, entry.Track);
        menu.ShowAt(HistoryList, e.GetPosition(HistoryList));
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e) => Playback.ClearQueue();

    private void UpNextList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is QueueEntry entry)
            entry.EnsureCover();
    }

    private void UpNextList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueEntry entry)
            Playback.PlayFromQueue(entry);
    }

    private void UpNextList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QueueEntry entry)
            return;

        var menu = new MenuFlyout();
        menu.Items.Add(TrackMenu.CreateItem("Play Now", "\uE768", () => Playback.PlayFromQueue(entry)));
        menu.Items.Add(TrackMenu.CreateItem("Remove from Queue", "\uE711", () => Playback.RemoveFromQueue(entry)));
        TrackMenu.AddGoToItems(menu, entry.Track);
        menu.ShowAt(UpNextList, e.GetPosition(UpNextList));
    }

    // ── MiniPlayer dan layar penuh ───────────────────────────

    // Jendela yang sedang menggantikan jendela utama (MiniPlayer atau layar penuh); paling banyak satu
    private Window? _replacement;
    private bool _isClosing;

    private void LcdCover_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        LcdCoverOverlay.Visibility = Visibility.Visible;

    private void LcdCover_PointerExited(object sender, PointerRoutedEventArgs e) =>
        LcdCoverOverlay.Visibility = Visibility.Collapsed;

    private void LcdCover_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Jendela ini akan disembunyikan, jadi efek hover-nya dimatikan dulu supaya tidak tertinggal saat muncul lagi
        LcdCoverOverlay.Visibility = Visibility.Collapsed;
        SetLcdTimesVisible(false);
        ShowMiniPlayer();
    }

    /// <summary>Membuka MiniPlayer. Dipakai cover LCD dan tombol keluar di layar penuh.</summary>
    public void ShowMiniPlayer() => ShowInstead(new MiniPlayerWindow());

    /// <summary>Membuka layar penuh di layar tempat MiniPlayer (atau jendela ini) sedang berada.</summary>
    public void ShowFullScreen() => ShowInstead(new NowPlayingWindow((_replacement ?? this).AppWindow.Id));

    /// <summary>
    /// Membuka pengaturan audio di Settings (tombol "Audio Quality Settings" di popup kualitas). Kalau sedang di
    /// MiniPlayer atau layar penuh, jendela itu ditutup dulu, supaya jendela utama muncul lagi.
    /// </summary>
    public void OpenAudioSettings()
    {
        _replacement?.Close();
        NavView.SelectedItem = NavView.SettingsItem;
        NavigateToSection("settings");
    }

    /// <summary>
    /// Jendela lain menggantikan jendela utama: jendela utama disembunyikan, lalu muncul lagi seperti semula begitu
    /// jendela itu ditutup. Kalau jendela pengganti lain sedang terbuka (MiniPlayer ⇄ layar penuh), jendela itu
    /// ditutup tanpa memunculkan jendela utama.
    /// </summary>
    private void ShowInstead(Window window)
    {
        Window? previous = _replacement;
        _replacement = window;
        window.Closed += (_, _) =>
        {
            // Ditutup karena diganti jendela pengganti lain: jendela utama tetap tersembunyi
            if (!ReferenceEquals(_replacement, window))
                return;

            _replacement = null;
            if (_isClosing)
                return;

            AppWindow.Show();
            Activate();
        };

        // Jendela pengganti tampil lebih dulu, supaya aplikasi tidak pernah sesaat tanpa jendela yang terlihat
        window.Activate();
        previous?.Close();
        AppWindow.Hide();
    }

    /// <summary>Membuka halaman di dalam menu yang sedang aktif; tombol back kembali ke halaman sebelumnya.</summary>
    public void ShowPage(Type page, object? parameter) => ContentFrame.Navigate(page, parameter);

    private void LcdBorder_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        ShowLcdMenu(LcdBorder, e.GetPosition(LcdBorder));

    private void LcdMore_Click(object sender, RoutedEventArgs e) =>
        ShowLcdMenu(LcdMoreButton, new Point(0, LcdMoreButton.ActualHeight));

    /// <summary>Menu LCD (klik kanan atau tombol •••): Go to Album, Go to Artist, Show in File Explorer.</summary>
    private void ShowLcdMenu(UIElement target, Point position)
    {
        if (Playback.CurrentTrack is not { } track)
            return;

        var menu = new MenuFlyout();
        TrackMenu.AddGoToItems(menu, track);
        menu.Items.RemoveAt(0); // pemisah di awal tidak perlu karena tidak ada item lain di atasnya
        menu.ShowAt(target, position);
    }

    // Waktu berjalan dan sisa waktu hanya tampil saat kursor di atas LCD, supaya LCD tetap lega seperti Apple Music
    private void LcdBorder_PointerEntered(object sender, PointerRoutedEventArgs e) => SetLcdTimesVisible(true);

    private void LcdBorder_PointerExited(object sender, PointerRoutedEventArgs e) => SetLcdTimesVisible(false);

    private void SetLcdTimesVisible(bool visible)
    {
        var visibility = visible && Playback.CurrentTrack is not null ? Visibility.Visible : Visibility.Collapsed;
        LcdElapsedText.Visibility = visibility;
        LcdRemainingText.Visibility = visibility;
    }


    // ── Navigasi ─────────────────────────────────────────────

    private string _currentSection = "";
    private readonly List<object> _folderMenuItems = [];

    private const string FolderTagPrefix = "folder:";

    /// <summary>
    /// Membuat ulang grup "Folders" di sidebar dari daftar folder di Settings.
    /// Dipanggil saat aplikasi dibuka dan setiap kali folder ditambah atau dihapus.
    /// </summary>
    public void RefreshFolderItems()
    {
        foreach (object item in _folderMenuItems)
            NavView.MenuItems.Remove(item);
        _folderMenuItems.Clear();

        var folders = App.Database.GetFolders();
        if (folders.Count == 0)
            return;

        _folderMenuItems.Add(new NavigationViewItemHeader { Content = "Folders" });
        foreach (string folder in folders)
        {
            var item = new NavigationViewItem
            {
                Content = LibraryPaths.DisplayName(folder),
                Tag = FolderTagPrefix + folder,
                Icon = new FontIcon { Glyph = "\uE8B7" },
            };
            ToolTipService.SetToolTip(item, folder); // path lengkap saat kursor diarahkan
            _folderMenuItems.Add(item);
        }

        foreach (object item in _folderMenuItems)
            NavView.MenuItems.Add(item);
    }

    /// <summary>
    /// Dipanggil setiap kali menu sidebar diklik, termasuk menu yang sedang terpilih.
    /// (SelectionChanged tidak terpanggil untuk menu yang sudah terpilih; itu penyebab menu Albums
    /// tidak merespons saat sedang membuka detail album.)
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        string tag = args.IsSettingsInvoked
            ? "settings"
            : args.InvokedItemContainer?.Tag as string ?? "songs";

        if (tag == _currentSection)
        {
            // Menu yang sama diklik lagi, misalnya Albums saat membuka detail album:
            // kembali ke halaman utama menu itu, dengan posisi scroll yang sama seperti tadi
            while (ContentFrame.CanGoBack)
                ContentFrame.GoBack();
            return;
        }

        NavigateToSection(tag);
    }

    private void NavigateToSection(string tag)
    {
        _currentSection = tag;

        switch (tag)
        {
            case "recent":
                ContentFrame.Navigate(typeof(AlbumsPage), "recent");
                break;
            case "albums":
                ContentFrame.Navigate(typeof(AlbumsPage), "albums");
                break;
            case "artists":
                ContentFrame.Navigate(typeof(ArtistsPage));
                break;
            case "genres":
                ContentFrame.Navigate(typeof(GenresPage));
                break;
            case not null when tag.StartsWith(FolderTagPrefix, StringComparison.Ordinal):
                ContentFrame.Navigate(typeof(FolderPage), tag[FolderTagPrefix.Length..]);
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

    // ── Pencarian ────────────────────────────────────────────

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Hanya bereaksi pada ketikan pengguna, bukan perubahan teks dari kode
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchTimer.Stop();
        ShowSearch(args.QueryText);
    }

    private void ShowSearch(string text)
    {
        string query = text.Trim();
        if (query.Length == 0)
            return;

        // Halaman hasil sudah terbuka: cukup perbarui isinya
        if (ContentFrame.Content is SearchPage searchPage)
        {
            searchPage.ShowResults(query);
            return;
        }

        _currentSection = "search";
        NavView.SelectedItem = null; // tidak ada menu sidebar yang aktif saat melihat hasil pencarian
        ContentFrame.Navigate(typeof(SearchPage), query);
        ContentFrame.BackStack.Clear();
        NavView.IsBackEnabled = false;
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }
}

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

/// <summary>
/// Lirik lagu yang sedang diputar. Lirik bertanda waktu: baris aktif menyala dan digulir ke posisi
/// <see cref="ActiveLinePosition"/>, baris lain redup, dan setiap baris bisa diklik. Lirik biasa: teks yang bisa digulir.
/// Isinya diambil dari <see cref="App.Lyrics"/>, jadi panel utama dan layar penuh selalu sama.
/// </summary>
public sealed partial class LyricsView : UserControl
{
    // Baris aktif dihitung ulang 10 kali per detik. Hitungannya dimajukan 0,1 detik, supaya jeda timer tidak membuat
    // baris menyala sesudah mulai dinyanyikan.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HighlightLead = TimeSpan.FromMilliseconds(100);

    // Setelah pengguna menggulir sendiri, lirik tidak digulir otomatis selama ini, supaya bisa dibaca dengan tenang
    private static readonly TimeSpan BrowseHold = TimeSpan.FromSeconds(4);

    // Indikator memuat baru muncul kalau pencarian lebih lama dari ini, supaya tidak berkedip untuk lirik dari file
    private static readonly TimeSpan LoadingRingDelay = TimeSpan.FromMilliseconds(400);

    private const double DimOpacity = 0.35;

    // Ukuran baris yang tidak sedang dinyanyikan, relatif terhadap baris aktif
    private static readonly Vector3 InactiveScale = new(0.95f, 0.95f, 1f);
    private const double PlainOpacity = 0.9;

    private readonly LyricsController _lyrics = App.Lyrics;
    private readonly PlaybackService _playback = App.Playback;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<FrameworkElement> _rows = [];

    // Pengisian karaoke per baris (null untuk lirik tanpa tanda waktu), urutannya sama dengan _rows
    private readonly List<KaraokeRow?> _karaoke = [];
    private readonly SmoothClock _clock = new();
    private bool _isRendering;

    // Latar baris saat kursor di atasnya: tanda bahwa baris itu bisa diklik. Latar transparan (bukan kosong) supaya
    // seluruh baris, termasuk sela di antara kata, bisa diklik.
    private readonly SolidColorBrush _rowBackground = new(Colors.Transparent);
    private readonly SolidColorBrush _rowHoverBackground = new(ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    // Tombol terjemahan menyala (latar terang, ikon gelap) selama ada lapisan yang tampil, seperti Apple Music
    private readonly SolidColorBrush _layersOnBackground = new(ColorHelper.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _layersOnForeground = new(ColorHelper.FromArgb(0xFF, 0x1C, 0x1C, 0x1C));
    private readonly SolidColorBrush _layersOffBackground = new(ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _layersOffForeground = new(Colors.White);

    private Lyrics? _shown;
    private bool _hasReading;
    private bool _hasTranslation;
    private TextBlock? _footer;
    private int _activeIndex = -1;
    private DateTime _browseUntil = DateTime.MinValue;
    private bool _recenterPending;
    private LyricsState _lastState = LyricsState.Idle;
    private DateTime _loadingSince;
    private bool _isLoaded;
    private bool _isActive;
    private bool _isRunning;

    public LyricsView()
    {
        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            UpdateRunning();
            LyricsDisplay.Changed += OnDisplayChanged;
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            UpdateRunning();
            LyricsDisplay.Changed -= OnDisplayChanged;
        };

        // ScrollViewer menangani sendiri roda mouse dan seret, jadi event-nya didengar dengan handledEventsToo.
        // Klik baris juga diawali PointerPressed, tapi Row_Tapped langsung membatalkan mode membaca ini.
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => StartBrowsing()), true);
        Scroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => StartBrowsing()), true);
        Scroller.DirectManipulationStarted += (_, _) => StartBrowsing();
    }

    /// <summary>Ukuran huruf baris lirik. Baris tambahan (misalnya terjemahan) 60% dari ini.</summary>
    public double LineFontSize { get; set; } = 22;

    /// <summary>Posisi baris aktif dari atas, sebagai bagian dari tinggi tampilan: 0,3 berarti sepertiga atas.</summary>
    public double ActiveLinePosition { get; set; } = 0.3;

    /// <summary>
    /// True untuk layar penuh: makin jauh dari baris aktif, makin redup, dan baris yang sudah lewat hampir hilang.
    /// False: semua baris selain baris aktif sama redupnya.
    /// </summary>
    public bool FadeByDistance { get; set; }

    /// <summary>
    /// True di jendela utama: tombol pensil dan Add Lyrics membuka editor lirik. MiniPlayer dan layar penuh hanya
    /// menampilkan lirik.
    /// </summary>
    public bool ShowEditButton { get; set; } = true;

    /// <summary>
    /// True selama tampilan ini dibuka pengguna (panel lirik atau lirik di layar penuh). Hanya saat aktif lirik dicari
    /// dan baris aktif diperbarui.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            UpdateRunning();
        }
    }

    private void UpdateRunning()
    {
        bool run = _isLoaded && _isActive;
        if (run == _isRunning)
            return;

        _isRunning = run;
        if (run)
        {
            _lyrics.Changed += OnLyricsChanged;
            _lyrics.AddViewer(); // bisa langsung memicu Changed, misalnya mulai mencari lirik
            Render();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            UpdateKaraokeLoop();
            _lyrics.Changed -= OnLyricsChanged;
            _lyrics.RemoveViewer();
        }
    }

    private void OnLyricsChanged(object? sender, EventArgs e) => Render();

    // Cara baca atau terjemahan dinyalakan/dimatikan (di panel ini atau panel lirik lain): baris digambar ulang
    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _shown = null;
        Render();
    }

    // Isi menu disesuaikan saat dibuka: "Hide" untuk lapisan yang sedang tampil, "Show" untuk yang tersembunyi
    private void LayersMenu_Opening(object sender, object e)
    {
        ReadingItem.Text = _hasReading && LyricsDisplay.ShowReading ? "Hide Reading" : "Show Reading";
        ReadingItem.IsEnabled = _hasReading;
        TranslationItem.Text = _hasTranslation && LyricsDisplay.ShowTranslation ? "Hide Translation" : "Show Translation";
        TranslationItem.IsEnabled = _hasTranslation;
    }

    private void ReadingItem_Click(object sender, RoutedEventArgs e) => LyricsDisplay.ShowReading = !LyricsDisplay.ShowReading;

    private void TranslationItem_Click(object sender, RoutedEventArgs e) => LyricsDisplay.ShowTranslation = !LyricsDisplay.ShowTranslation;

    // Editor lirik di jendela utama (MiniPlayer dan layar penuh ikut ditutup oleh ShowPage)
    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Playback.CurrentTrack is { } track)
            App.MainWindow.ShowPage(typeof(Pages.LyricsEditorPage), track);
    }

    /// <summary>
    /// Tombol terjemahan hanya untuk lirik yang punya cara baca atau terjemahan; pensil selama ada lirik, dan hanya di
    /// jendela utama.
    /// </summary>
    private void UpdateToolbar(Lyrics? lyrics)
    {
        _hasReading = lyrics?.Lines.Any(l => l.Reading is not null) == true;
        _hasTranslation = lyrics?.Lines.Any(l => l.Translation is not null) == true;
        bool showing = (_hasReading && LyricsDisplay.ShowReading) || (_hasTranslation && LyricsDisplay.ShowTranslation);
        LayersButton.Background = showing ? _layersOnBackground : _layersOffBackground;
        LayersButton.Foreground = showing ? _layersOnForeground : _layersOffForeground;
        LayersButton.Visibility = _hasReading || _hasTranslation ? Visibility.Visible : Visibility.Collapsed;
        EditButton.Visibility = ShowEditButton && lyrics is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        var state = _lyrics.State;
        if (state == LyricsState.Loading && _lastState != LyricsState.Loading)
            _loadingSince = DateTime.UtcNow;
        _lastState = state;

        var current = state == LyricsState.Ready ? _lyrics.Current : null;
        if (!ReferenceEquals(current?.Lyrics, _shown))
            BuildRows(current?.Lyrics);

        if (current is not null)
            UpdateFooter();

        ShowMessage(state);
        UpdateLoadingRing();
    }

    private void ShowMessage(LyricsState state)
    {
        (string title, string detail) = state switch
        {
            LyricsState.Idle => ("Lyrics", "Play a song to see its lyrics here."),
            LyricsState.NotFound => ("No lyrics for this song", ShowEditButton
                ? "Add them yourself with Add Lyrics, or put a .lrc file with the same name next to the song file."
                : "Add them with Add Lyrics in the main window, or put a .lrc file with the same name next to the song file."),
            LyricsState.Instrumental => ("Instrumental", "LRCLIB lists this song as instrumental."),
            LyricsState.Unavailable => ("Couldn't look up lyrics",
                $"{_lyrics.ErrorMessage} Check your internet connection, then try again.".Trim()),
            _ => ("", ""),
        };

        MessageTitle.Text = title;
        MessageDetail.Text = detail;
        MessagePanel.Visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = state == LyricsState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        AddLyricsButton.Visibility = ShowEditButton
            && state is LyricsState.NotFound or LyricsState.Instrumental or LyricsState.Unavailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateLoadingRing()
    {
        bool show = _lyrics.State == LyricsState.Loading && DateTime.UtcNow - _loadingSince >= LoadingRingDelay;
        LoadingRing.IsActive = show;
        LoadingRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildRows(Lyrics? lyrics)
    {
        _shown = lyrics;
        UpdateToolbar(lyrics);
        if (lyrics is not null && App.Playback.CurrentTrack is { } track)
            _ = LoadCreditsAsync(track.FilePath);
        _rows.Clear();
        _karaoke.Clear();
        LinesPanel.Children.Clear();
        _footer = null;
        _activeIndex = -1;
        _browseUntil = DateTime.MinValue;
        _recenterPending = false;
        if (lyrics is null)
            return;

        for (int i = 0; i < lyrics.Lines.Count; i++)
        {
            var row = CreateRow(lyrics, i);
            _rows.Add(row);
            LinesPanel.Children.Add(row);
        }

        // Keterangan asal lirik di paling bawah, seperti nama penulis lagu di Apple Music
        _footer = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, LineFontSize, 12, 0),
        };
        LinesPanel.Children.Add(_footer);

        UpdatePadding();
        _activeIndex = lyrics.IndexAt(_playback.Position + HighlightLead);
        ApplyOpacities();
        FillActive();
        UpdateKaraokeLoop();

        // Posisi baris baru diketahui setelah layout, jadi layout dijalankan sekarang sebelum menggulir
        Scroller.UpdateLayout();
        if (lyrics.IsSynced)
            ScrollToActive(animate: false);
        else
            Scroller.ChangeView(null, 0, null, true);
    }

    private FrameworkElement CreateRow(Lyrics lyrics, int index)
    {
        var line = lyrics.Lines[index];
        bool synced = lyrics.IsSynced;

        // Pemisah bait di lirik biasa: cukup ruang kosong
        if (!synced && line.IsBlank)
        {
            _karaoke.Add(null);
            return new Border { Height = LineFontSize * 0.7 };
        }

        var content = new StackPanel { Spacing = 2 };
        var main = new TextBlock
        {
            FontSize = LineFontSize,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(main);

        // Baris kosong di lirik bertanda waktu adalah jeda instrumental
        string text = line.IsBlank ? "•  •  •" : line.Text;
        KaraokeText? mainFill = null, readingFill = null;
        if (synced)
            mainFill = new KaraokeText(main,
                KaraokeTiming.Plan(text, line.Words, line.WordsEnd, TimeUntilNext(lyrics, index), fillWholeGap: line.IsBlank));
        else
            main.Text = text;

        // Urutan seperti Apple Music: lirik asli, cara baca, lalu terjemahan (lapisan dari editor lirik). Cara baca ikut
        // diisi bersama lirik aslinya; terjemahan tidak.
        if (LyricsDisplay.ShowReading && !line.IsBlank && line.Reading is { } reading)
        {
            var readingLine = SmallLine(synced ? "" : reading, FontWeights.SemiBold);
            if (synced)
                readingFill = new KaraokeText(readingLine, KaraokeTiming.Spread(reading, 1)); // diisi sejauh lirik aslinya
            content.Children.Add(readingLine);
        }
        foreach (string extra in line.Extra)
            content.Children.Add(SmallLine(extra, FontWeights.SemiBold));
        if (LyricsDisplay.ShowTranslation && !line.IsBlank && line.Translation is { } translation)
            content.Children.Add(SmallLine(translation, FontWeights.Normal));

        double vertical = Math.Round(LineFontSize * 0.35);
        var row = new Border
        {
            Child = content,
            Padding = new Thickness(12, vertical, 12, vertical),
            CornerRadius = new CornerRadius(8),
            Background = _rowBackground,
            Tag = index,
            // Perubahan kecerahan dan ukuran dianimasikan, supaya perpindahan baris terasa halus
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(250) },
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(300) },
        };

        _karaoke.Add(mainFill is null
            ? null
            : new KaraokeRow(mainFill, readingFill, line.Time, TimeSpan.FromSeconds(mainFill.Duration)));

        if (synced)
        {
            row.Tapped += Row_Tapped;
            row.PointerEntered += (_, _) => row.Background = _rowHoverBackground;
            row.PointerExited += (_, _) => row.Background = _rowBackground;
        }
        return row;
    }

    /// <summary>
    /// Lirik bertanda waktu diberi ruang kosong di atas dan bawah, supaya baris pertama dan terakhir pun bisa digulir
    /// sampai ke posisi baris aktif. Lirik biasa diberi ruang di atas untuk tombol di kanan atas, supaya baris
    /// pertamanya tidak tertutup.
    /// </summary>
    private void UpdatePadding()
    {
        double height = Scroller.ActualHeight;
        bool hasToolbar = LayersButton.Visibility == Visibility.Visible || EditButton.Visibility == Visibility.Visible;
        LinesPanel.Padding = _shown is { IsSynced: true } && height > 0
            ? new Thickness(0, height * ActiveLinePosition, 0, height * (1 - ActiveLinePosition))
            : new Thickness(0, hasToolbar ? 64 : 16, 0, 24);
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Misalnya jendela diperbesar, atau lirik di layar penuh baru muncul
        UpdatePadding();
        Scroller.UpdateLayout();
        ScrollToActive(animate: false);
    }

    private void OnTick()
    {
        UpdateLoadingRing();
        UpdateActiveLine(force: false);

        // Selama lagu diputar, pengisian karaoke diperbarui setiap frame (OnRendering). Saat berhenti posisinya tetap
        // bisa berubah (misalnya digeser di progress bar), jadi diperbarui di sini.
        UpdateKaraokeLoop();
        if (!_isRendering)
            FillActive();
    }

    // ── Karaoke ──────────────────────────────────────────────

    /// <summary>
    /// Waktu sampai baris berikutnya (termasuk baris jeda) menyala, batas pengisian baris ini. Baris berikutnya menyala
    /// HighlightLead lebih awal, jadi jarak itu dikurangi dulu supaya pengisian sempat selesai. Titik jeda instrumental
    /// diisi selama seluruh jarak ini, seperti hitungan mundur.
    /// </summary>
    private TimeSpan TimeUntilNext(Lyrics lyrics, int index)
    {
        var line = lyrics.Lines[index];
        var next = index + 1 < lyrics.Lines.Count ? lyrics.Lines[index + 1].Time : TimeSpan.FromSeconds(_playback.DurationSeconds);
        var gap = next > line.Time ? next - line.Time : TimeSpan.FromSeconds(5);
        return TimeSpan.FromSeconds(Math.Max((gap - HighlightLead).TotalSeconds, 0.1));
    }

    /// <summary>
    /// Baris yang dinyanyikan diisi (FillActive). Baris sesudahnya sudah diredupkan seperti bagian yang belum
    /// dinyanyikan, dengan kecerahan barisnya dinaikkan sebanding (lihat OpacityFor), jadi tampilannya tetap sama dan
    /// saat baris itu mulai dinyanyikan hanya kecerahan barisnya yang naik, tanpa sempat meredup. Baris lain teks biasa.
    /// </summary>
    private void PrepareKaraoke()
    {
        for (int i = 0; i < _karaoke.Count; i++)
        {
            if (i == _activeIndex || _karaoke[i] is not { } row)
                continue;
            if (IsUpcomingKaraoke(i))
                row.Dim();
            else
                row.Reset();
        }
    }

    private bool IsUpcomingKaraoke(int index) =>
        index == _activeIndex + 1 && index < _karaoke.Count && _karaoke[index] is not null;

    private void FillActive()
    {
        if (_activeIndex < 0 || _activeIndex >= _karaoke.Count || _karaoke[_activeIndex] is not { } row)
            return;

        var position = _clock.Read(_playback.Position, _playback.IsPlaying);
        row.Fill((position - row.Start).TotalSeconds);
    }

    /// <summary>
    /// Diisi setiap frame hanya selama tampilan aktif dan terlihat (jendela tidak diminimalkan), lagu diputar, dan baris
    /// yang dinyanyikan punya karaoke yang belum penuh. Selebihnya cukup OnTick 10 kali per detik.
    /// </summary>
    private void UpdateKaraokeLoop()
    {
        bool run = _isRunning && _playback.IsPlaying && XamlRoot?.IsHostVisible == true
            && _activeIndex >= 0 && _activeIndex < _karaoke.Count
            && _karaoke[_activeIndex] is { } row && _playback.Position < row.Start + row.Duration;
        if (run == _isRendering)
            return;

        _isRendering = run;
        if (run)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        FillActive();
        UpdateKaraokeLoop(); // baris sudah penuh: berhenti sampai baris berikutnya
    }

    private sealed record KaraokeRow(KaraokeText Main, KaraokeText? Reading, TimeSpan Start, TimeSpan Duration)
    {
        /// <summary>Mengisi sampai <paramref name="seconds"/> detik sejak baris mulai; cara baca ikut sejauh lirik aslinya.</summary>
        public void Fill(double seconds)
        {
            double sung = Main.Fill(seconds);
            Reading?.Fill(sung);
        }

        /// <summary>Semua huruf redup seperti belum dinyanyikan.</summary>
        public void Dim()
        {
            Main.Fill(double.NegativeInfinity);
            Reading?.Fill(double.NegativeInfinity);
        }

        public void Reset()
        {
            Main.Reset();
            Reading?.Reset();
        }
    }

    /// <summary>
    /// Posisi lagu yang bergerak mulus setiap frame. Posisi dari engine naik bertahap per blok audio yang dibaca, jadi
    /// di antara pembacaan itu posisinya diteruskan dengan Stopwatch dan pelan-pelan disesuaikan ke posisi engine.
    /// Lompatan besar (seek, ganti lagu) langsung diikuti.
    /// </summary>
    private sealed class SmoothClock
    {
        private static readonly TimeSpan JumpThreshold = TimeSpan.FromMilliseconds(250);
        private const double Correction = 0.1;

        private readonly Stopwatch _watch = new();
        private TimeSpan _anchor;

        public TimeSpan Read(TimeSpan raw, bool playing)
        {
            if (!playing || !_watch.IsRunning)
                return Restart(raw, playing);

            var predicted = _anchor + _watch.Elapsed;
            var error = raw - predicted;
            if (error.Duration() > JumpThreshold)
                return Restart(raw, playing);

            _anchor += error * Correction;
            return predicted + error * Correction;
        }

        private TimeSpan Restart(TimeSpan raw, bool playing)
        {
            _anchor = raw;
            if (playing)
                _watch.Restart();
            else
                _watch.Reset();
            return raw;
        }
    }

    private void UpdateActiveLine(bool force)
    {
        if (_shown is not { IsSynced: true } lyrics)
            return;

        bool browsing = DateTime.UtcNow < _browseUntil;
        int index = lyrics.IndexAt(_playback.Position + HighlightLead);
        if (index != _activeIndex || force)
        {
            // Baris baru langsung diisi sesuai posisi lagu; yang selesai dinyanyikan kembali ke teks biasa (ApplyOpacities)
            _activeIndex = index;
            ApplyOpacities();
            FillActive();
            if (!browsing)
                ScrollToActive(animate: true);
        }
        else if (_recenterPending && !browsing)
        {
            // Pengguna selesai menggulir sendiri: kembali ke baris yang sedang dinyanyikan
            _recenterPending = false;
            ScrollToActive(animate: true);
        }
    }

    private void ApplyOpacities()
    {
        PrepareKaraoke();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            double opacity = OpacityFor(i);
            // Baris berikutnya sudah diredupkan per huruf (PrepareKaraoke), jadi kecerahan barisnya diimbangi
            row.Opacity = IsUpcomingKaraoke(i) ? Math.Min(1, opacity / KaraokeText.UnsungOpacity) : opacity;

            // Baris aktif tampil penuh, baris lain sedikit mengecil; diskalakan dari tepi kiri, tempat teksnya dimulai
            row.CenterPoint = new Vector3(0, (float)(row.ActualHeight / 2), 0);
            row.Scale = _shown is { IsSynced: true } && i != _activeIndex ? InactiveScale : Vector3.One;
        }
    }

    private double OpacityFor(int index)
    {
        if (_shown is not { IsSynced: true })
            return PlainOpacity;
        if (index == _activeIndex)
            return 1;
        if (!FadeByDistance)
            return DimOpacity;

        // Sebelum baris pertama (_activeIndex -1), baris pertama dianggap baris berikutnya
        int distance = index - _activeIndex;
        return distance switch
        {
            < 0 => 0.15,
            1 => 0.55,
            2 => 0.4,
            _ => 0.28,
        };
    }

    private void ScrollToActive(bool animate)
    {
        if (_shown is not { IsSynced: true } || _rows.Count == 0)
            return;

        var row = _rows[Math.Max(_activeIndex, 0)];
        double target = row.ActualOffset.Y - Scroller.ViewportHeight * ActiveLinePosition;
        Scroller.ChangeView(null, Math.Max(0, target), null, !animate);
    }

    private void StartBrowsing()
    {
        _browseUntil = DateTime.UtcNow + BrowseHold;
        _recenterPending = true;
    }

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int index } || _shown is not { IsSynced: true } lyrics || index >= lyrics.Lines.Count)
            return;

        // Klik baris: lompat ke baris itu dan putar, seperti Apple Music
        _browseUntil = DateTime.MinValue;
        _recenterPending = false;
        _playback.SeekTo(lyrics.Lines[index].Time);
        if (!_playback.IsPlaying)
            _playback.TogglePlayPause();
        UpdateActiveLine(force: true);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _lyrics.Retry();

    /// <summary>Baris kecil di bawah lirik asli: cara baca, terjemahan, atau baris dwibahasa dari file LRC.</summary>
    private TextBlock SmallLine(string text, Windows.UI.Text.FontWeight weight) => new()
    {
        Text = text,
        FontSize = Math.Round(LineFontSize * 0.6),
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
    };

    // Kredit penulis lagu di bawah lirik ("Written by: ..."), dibaca dari tag LYRICIST dan COMPOSER lagunya
    private string? _creditsPath;
    private string? _writtenBy;

    private async System.Threading.Tasks.Task LoadCreditsAsync(string path)
    {
        if (_creditsPath == path)
        {
            UpdateFooter(); // baris digambar ulang untuk lagu yang sama: kreditnya sudah ada
            return;
        }
        _creditsPath = path;
        _writtenBy = null;
        string? credits = await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var tags = TagEditor.Read(path);
                return SongCredits.WrittenBy(tags.Get("lyricist"), tags.Get("composer"));
            }
            catch (Exception)
            {
                return null; // file tidak bisa dibaca: lirik tetap tampil tanpa kredit
            }
        });
        if (_creditsPath != path)
            return; // lagu sudah berganti selama tag dibaca
        _writtenBy = credits;
        UpdateFooter();
    }

    /// <summary>
    /// Di bawah lirik, seperti Apple Music: penulis lagunya. Sumber lirik (tag, .lrc, LRCLIB) tidak ditampilkan di
    /// sini; keterangannya ada di editor lirik. Selama lirik bertanda waktu dicari di LRCLIB, keterangannya ditambahkan.
    /// </summary>
    private void UpdateFooter()
    {
        if (_footer is null)
            return;
        var lines = new List<string>();
        if (_writtenBy is not null)
            lines.Add(_writtenBy);
        if (_lyrics.IsCheckingOnline)
            lines.Add("Looking for time-synced lyrics on LRCLIB…");
        _footer.Text = string.Join("\n", lines);
        _footer.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

using System;
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

    // Latar baris saat kursor di atasnya: tanda bahwa baris itu bisa diklik. Latar transparan (bukan kosong) supaya
    // seluruh baris, termasuk sela di antara kata, bisa diklik.
    private readonly SolidColorBrush _rowBackground = new(Colors.Transparent);
    private readonly SolidColorBrush _rowHoverBackground = new(ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    private Lyrics? _shown;
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

    private void ReadingToggle_Click(object sender, RoutedEventArgs e) => LyricsDisplay.ShowReading = ReadingToggle.IsChecked == true;

    private void TranslationToggle_Click(object sender, RoutedEventArgs e) => LyricsDisplay.ShowTranslation = TranslationToggle.IsChecked == true;

    // Editor lirik di jendela utama (MiniPlayer dan layar penuh ikut ditutup oleh ShowPage)
    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Playback.CurrentTrack is { } track)
            App.MainWindow.ShowPage(typeof(Pages.LyricsEditorPage), track);
    }

    /// <summary>Tombol cara baca dan terjemahan hanya untuk lirik yang punya lapisan itu; pensil selama ada lagu.</summary>
    private void UpdateToolbar(Lyrics? lyrics)
    {
        bool hasReading = lyrics?.Lines.Any(l => l.Reading is not null) == true;
        bool hasTranslation = lyrics?.Lines.Any(l => l.Translation is not null) == true;
        ReadingToggle.Visibility = hasReading ? Visibility.Visible : Visibility.Collapsed;
        TranslationToggle.Visibility = hasTranslation ? Visibility.Visible : Visibility.Collapsed;
        ReadingToggle.IsChecked = LyricsDisplay.ShowReading;
        TranslationToggle.IsChecked = LyricsDisplay.ShowTranslation;
        Toolbar.Visibility = lyrics is not null ? Visibility.Visible : Visibility.Collapsed;
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
            LyricsState.NotFound => ("No lyrics for this song",
                "Add them yourself with Add Lyrics, or put a .lrc file with the same name next to the song file."),
            LyricsState.Instrumental => ("Instrumental", "LRCLIB lists this song as instrumental."),
            LyricsState.Unavailable => ("Couldn't look up lyrics",
                $"{_lyrics.ErrorMessage} Check your internet connection, then try again.".Trim()),
            _ => ("", ""),
        };

        MessageTitle.Text = title;
        MessageDetail.Text = detail;
        MessagePanel.Visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = state == LyricsState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        AddLyricsButton.Visibility = state is LyricsState.NotFound or LyricsState.Instrumental or LyricsState.Unavailable
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
        LinesPanel.Children.Clear();
        _footer = null;
        _activeIndex = -1;
        _browseUntil = DateTime.MinValue;
        _recenterPending = false;
        if (lyrics is null)
            return;

        for (int i = 0; i < lyrics.Lines.Count; i++)
        {
            var row = CreateRow(lyrics.Lines[i], i, lyrics.IsSynced);
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

        // Posisi baris baru diketahui setelah layout, jadi layout dijalankan sekarang sebelum menggulir
        Scroller.UpdateLayout();
        if (lyrics.IsSynced)
            ScrollToActive(animate: false);
        else
            Scroller.ChangeView(null, 0, null, true);
    }

    private FrameworkElement CreateRow(LyricLine line, int index, bool synced)
    {
        // Pemisah bait di lirik biasa: cukup ruang kosong
        if (!synced && line.IsBlank)
            return new Border { Height = LineFontSize * 0.7 };

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            // Baris kosong di lirik bertanda waktu adalah jeda instrumental
            Text = line.IsBlank ? "•  •  •" : line.Text,
            FontSize = LineFontSize,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        // Urutan seperti Apple Music: lirik asli, cara baca, lalu terjemahan (lapisan dari editor lirik)
        if (LyricsDisplay.ShowReading && !line.IsBlank && line.Reading is { } reading)
            content.Children.Add(SmallLine(reading, FontWeights.SemiBold));
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
    /// sampai ke posisi baris aktif.
    /// </summary>
    private void UpdatePadding()
    {
        double height = Scroller.ActualHeight;
        LinesPanel.Padding = _shown is { IsSynced: true } && height > 0
            ? new Thickness(0, height * ActiveLinePosition, 0, height * (1 - ActiveLinePosition))
            : new Thickness(0, 16, 0, 24);
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
    }

    private void UpdateActiveLine(bool force)
    {
        if (_shown is not { IsSynced: true } lyrics)
            return;

        bool browsing = DateTime.UtcNow < _browseUntil;
        int index = lyrics.IndexAt(_playback.Position + HighlightLead);
        if (index != _activeIndex || force)
        {
            _activeIndex = index;
            ApplyOpacities();
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
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.Opacity = OpacityFor(i);

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

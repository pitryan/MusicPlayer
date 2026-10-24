using System;
using System.Numerics;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            UpdateRunning();
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

    private void Render()
    {
        var state = _lyrics.State;
        if (state == LyricsState.Loading && _lastState != LyricsState.Loading)
            _loadingSince = DateTime.UtcNow;
        _lastState = state;

        var current = state == LyricsState.Ready ? _lyrics.Current : null;
        if (!ReferenceEquals(current?.Lyrics, _shown))
            BuildRows(current?.Lyrics);

        if (_footer is not null && current is not null)
        {
            _footer.Text = _lyrics.IsCheckingOnline
                ? $"{LyricsController.DescribeSource(current)}\nLooking for time-synced lyrics on LRCLIB…"
                : LyricsController.DescribeSource(current);
        }

        ShowMessage(state);
        UpdateLoadingRing();
    }

    private void ShowMessage(LyricsState state)
    {
        (string title, string detail) = state switch
        {
            LyricsState.Idle => ("Lyrics", "Play a song to see its lyrics here."),
            LyricsState.NotFound => ("No lyrics for this song",
                "Add lyrics to the song's tags, or put a .lrc file with the same name next to the song file."),
            LyricsState.Instrumental => ("Instrumental", "LRCLIB lists this song as instrumental."),
            LyricsState.Unavailable => ("Couldn't look up lyrics",
                $"{_lyrics.ErrorMessage} Check your internet connection, then try again.".Trim()),
            _ => ("", ""),
        };

        MessageTitle.Text = title;
        MessageDetail.Text = detail;
        MessagePanel.Visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = state == LyricsState.Unavailable ? Visibility.Visible : Visibility.Collapsed;
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
        foreach (string extra in line.Extra)
        {
            content.Children.Add(new TextBlock
            {
                Text = extra,
                FontSize = Math.Round(LineFontSize * 0.6),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        }

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
}

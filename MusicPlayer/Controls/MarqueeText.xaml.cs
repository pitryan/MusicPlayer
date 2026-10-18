using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MusicPlayer.Controls;

/// <summary>
/// Teks satu baris seperti di LCD Apple Music: kalau muat, tampil di tengah; kalau terlalu panjang,
/// diam sebentar, bergeser perlahan sampai ujungnya terlihat, diam lagi, lalu kembali ke awal.
/// </summary>
public sealed partial class MarqueeText : UserControl
{
    private const double PixelsPerSecond = 30;
    private const double PauseAtStartSeconds = 2.0;
    private const double PauseAtEndSeconds = 1.5;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText), new PropertyMetadata("", OnTextChanged));

    private readonly Stopwatch _clock = new();
    private double _scrollDistance;
    private bool _isScrolling;

    public MarqueeText()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => StopScrolling();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarqueeText)sender;
        control.Label.Text = e.NewValue as string ?? "";
        control.Refresh();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

    /// <summary>Ukur ulang teks, lalu putuskan: tampil di tengah atau perlu digeser.</summary>
    private void Refresh()
    {
        Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = Label.DesiredSize.Width;
        double textHeight = Label.DesiredSize.Height;
        double available = Root.ActualWidth;

        // Canvas tidak punya tinggi sendiri, jadi tingginya disamakan dengan teks.
        // Clip memotong teks yang keluar dari area kontrol.
        Root.Height = textHeight;
        Root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, available), textHeight) };

        if (available <= 0 || textWidth <= available)
        {
            StopScrolling();
            Canvas.SetLeft(Label, Math.Max(0, (available - textWidth) / 2));
        }
        else
        {
            Canvas.SetLeft(Label, 0);
            _scrollDistance = textWidth - available;
            StartScrolling();
        }
    }

    private void StartScrolling()
    {
        _clock.Restart();
        Offset.X = 0;
        if (!_isScrolling)
        {
            CompositionTarget.Rendering += OnRendering; // dipanggil setiap frame
            _isScrolling = true;
        }
    }

    private void StopScrolling()
    {
        if (_isScrolling)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isScrolling = false;
        }
        Offset.X = 0;
    }

    private void OnRendering(object? sender, object e)
    {
        double scrollSeconds = _scrollDistance / PixelsPerSecond;
        double cycleSeconds = PauseAtStartSeconds + scrollSeconds + PauseAtEndSeconds;
        double t = _clock.Elapsed.TotalSeconds % cycleSeconds;

        Offset.X = t <= PauseAtStartSeconds
            ? 0
            : -Math.Min(_scrollDistance, (t - PauseAtStartSeconds) * PixelsPerSecond);
    }
}

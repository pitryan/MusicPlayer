using System;
using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using MusicPlayer.Services;
using Windows.UI.ViewManagement;

namespace MusicPlayer.Controls;

/// <summary>
/// Batang "sedang diputar" di baris lagu (lihat XAML-nya). Animasinya lewat Composition, jadi tetap halus walaupun UI
/// thread sibuk, dan hanya berjalan selama kontrol tampil dan lagu berbunyi. Baris daftar lagu didaur ulang saat
/// di-scroll, jadi langganan ke PlaybackService selalu dilepas lagi saat Unloaded.
/// </summary>
public sealed partial class PlayingBars : UserControl
{
    private const float BarHeight = 13f;

    // Satu instance cukup; AnimationsEnabled dibaca ulang setiap kali status putar berubah
    private static readonly UISettings SystemSettings = new();

    // Tinggi diam (skala 0–1) saat lagu dijeda: sengaja berbeda-beda, seperti Apple Music
    private static readonly float[] RestHeights = [0.45f, 0.85f, 0.6f, 0.35f];

    // Irama tiap batang: durasi berbeda dan tidak saling habis dibagi, supaya geraknya tidak terlihat seragam.
    // Setiap irama mulai dan berakhir di tinggi diamnya, jadi putaran berikutnya dan jeda tidak meloncat.
    private static readonly (double Seconds, (float Progress, float Height)[] Frames)[] Rhythms =
    {
        (0.95, new (float, float)[] { (0f, 0.45f), (0.3f, 1f), (0.55f, 0.3f), (0.8f, 0.75f), (1f, 0.45f) }),
        (1.25, new (float, float)[] { (0f, 0.85f), (0.25f, 0.35f), (0.5f, 1f), (0.75f, 0.5f), (1f, 0.85f) }),
        (0.8, new (float, float)[] { (0f, 0.6f), (0.35f, 0.25f), (0.7f, 0.95f), (1f, 0.6f) }),
        (1.1, new (float, float)[] { (0f, 0.35f), (0.3f, 0.8f), (0.5f, 0.5f), (0.8f, 1f), (1f, 0.35f) }),
    };

    private Visual[] _visuals = [];
    private bool _isSubscribed;
    private bool _isAnimating;

    public PlayingBars()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UIElement[] bars = [Bar1, Bar2, Bar3, Bar4];
        _visuals = new Visual[bars.Length];
        for (int i = 0; i < bars.Length; i++)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bars[i]);
            visual.CenterPoint = new Vector3(0, BarHeight, 0); // dasar batang, supaya batang tumbuh ke atas
            _visuals[i] = visual;
        }

        // Loaded bisa terpanggil dua kali tanpa Unloaded di antaranya; jangan berlangganan dobel
        if (!_isSubscribed)
        {
            App.Playback.PropertyChanged += Playback_PropertyChanged;
            _isSubscribed = true;
        }

        _isAnimating = false;
        Refresh(settle: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            App.Playback.PropertyChanged -= Playback_PropertyChanged;
            _isSubscribed = false;
        }

        foreach (var visual in _visuals)
            visual.StopAnimation("Scale.Y");
        _isAnimating = false;
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.IsPlaying))
            Refresh(settle: true);
    }

    /// <summary>Jalankan atau hentikan animasi sesuai status putar dan setelan "Animation effects" Windows.</summary>
    private void Refresh(bool settle)
    {
        bool animationsOn = SystemSettings.AnimationsEnabled;
        bool run = App.Playback.IsPlaying && animationsOn;
        if (run && _isAnimating)
            return; // sudah berjalan; jangan mulai ulang dari awal

        _isAnimating = run;
        for (int i = 0; i < _visuals.Length; i++)
        {
            var visual = _visuals[i];
            visual.StopAnimation("Scale.Y");

            if (run)
                visual.StartAnimation("Scale.Y", Bounce(visual.Compositor, i));
            else if (settle && animationsOn)
                visual.StartAnimation("Scale.Y", SettleTo(visual.Compositor, RestHeights[i])); // turun pelan ke posisi diam
            else
                visual.Scale = new Vector3(1f, RestHeights[i], 1f);
        }
    }

    private static ScalarKeyFrameAnimation Bounce(Compositor compositor, int bar)
    {
        var (seconds, frames) = Rhythms[bar];
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0f), new Vector2(0.58f, 1f));

        var animation = compositor.CreateScalarKeyFrameAnimation();
        foreach (var (progress, height) in frames)
            animation.InsertKeyFrame(progress, height, ease);
        animation.Duration = TimeSpan.FromSeconds(seconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        return animation;
    }

    private static ScalarKeyFrameAnimation SettleTo(Compositor compositor, float height)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1f, height); // mulai dari tinggi saat ini
        animation.Duration = TimeSpan.FromMilliseconds(220);
        return animation;
    }
}

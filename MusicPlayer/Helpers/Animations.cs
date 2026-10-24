using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;

namespace MusicPlayer.Helpers;

/// <summary>
/// Animasi kecil yang dipakai di seluruh aplikasi, lewat Composition (berjalan di thread komposisi, jadi tetap halus
/// walaupun UI thread sedang sibuk). Semuanya singkat (150–300 ms) dengan kurva yang cepat di awal lalu melambat,
/// seperti Apple Music. Elemen yang dianimasikan di sini tidak boleh memakai properti Opacity/Scale XAML yang
/// dianimasikan juga (misalnya OpacityTransition), supaya keduanya tidak saling menimpa.
/// </summary>
public static class Animations
{
    /// <summary>Transisi halaman "masuk ke dalam": membuka album, artis, playlist, subfolder, editor.</summary>
    public static NavigationTransitionInfo DrillIn => new DrillInNavigationTransitionInfo();

    private static CompositionEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.0f), new Vector2(0.0f, 1.0f));

    private static Visual VisualOf(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    /// <summary>Muncul dari transparan, untuk isi yang baru berganti (misalnya cover dan judul saat lagu berganti).</summary>
    public static void FadeIn(UIElement element, double milliseconds = 300)
    {
        var visual = VisualOf(element);
        visual.StartAnimation("Opacity", Fade(visual.Compositor, milliseconds));
    }

    /// <summary>Masuk sambil bergeser dari (fromX, fromY) piksel dan memudar, untuk panel yang baru dibuka.</summary>
    public static void SlideIn(UIElement element, float fromX, float fromY, double milliseconds = 280)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = VisualOf(element);
        var compositor = visual.Compositor;

        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0f, new Vector3(fromX, fromY, 0));
        move.InsertKeyFrame(1f, Vector3.Zero, Ease(compositor));
        move.Duration = TimeSpan.FromMilliseconds(milliseconds);

        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Opacity", Fade(compositor, milliseconds));
    }

    /// <summary>
    /// Membesar sedikit saat kursor di atasnya, dan kembali saat kursor pergi. Untuk kotak album, artis, playlist,
    /// dan genre; dipanggil dari PointerEntered dan PointerExited.
    /// </summary>
    public static void Hover(UIElement element, bool hovering)
    {
        var visual = VisualOf(element);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(element.ActualSize / 2, 0);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, hovering ? new Vector3(1.03f, 1.03f, 1f) : Vector3.One, Ease(compositor));
        scale.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Denyut singkat: mengecil lalu kembali sedikit melewati ukuran asli. Untuk tombol Play/Pause.</summary>
    public static void Pop(UIElement element)
    {
        var visual = VisualOf(element);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(element.ActualSize / 2, 0);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.82f, 0.82f, 1f));
        scale.InsertKeyFrame(0.6f, new Vector3(1.06f, 1.06f, 1f), Ease(compositor));
        scale.InsertKeyFrame(1f, Vector3.One, Ease(compositor));
        scale.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation("Scale", scale);
    }

    private static ScalarKeyFrameAnimation Fade(Compositor compositor, double milliseconds)
    {
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, Ease(compositor));
        fade.Duration = TimeSpan.FromMilliseconds(milliseconds);
        return fade;
    }
}

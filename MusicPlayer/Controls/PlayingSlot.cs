using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MusicPlayer.Controls;

/// <summary>
/// Tempat batang "sedang diputar" (PlayingBars) di baris lagu. Tersembunyi, dan PlayingBars baru dibuat, saat
/// <see cref="IsPlaying"/> true, jadi baris lain tidak membawa kontrol beranimasi. Background dan CornerRadius-nya bisa
/// dipakai untuk menggelapkan cover di bawahnya, yang ikut tampil hanya untuk lagu yang sedang diputar.
/// </summary>
/// <remarks>
/// Menggantikan x:Load="{x:Bind IsPlaying}", yang membuat aplikasi crash. Kode x:Bind buatan WinUI untuk x:Load memegang
/// akar baris lewat WeakReference dan memakainya tanpa memeriksa null. Baris yang tampilannya sudah dibuang (misalnya blok
/// album yang didaur ulang ItemsRepeater) tetap menerima perubahan IsPlaying dari TrackRow-nya, lalu crash begitu akar
/// barisnya dibersihkan GC. Untuk properti biasa seperti IsPlaying di sini, kode buatan WinUI memegang elemennya secara
/// kuat, jadi perubahan itu hanya mengenai elemen yang sudah tidak tampil.
/// </remarks>
public sealed class PlayingSlot : Grid
{
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(PlayingSlot), new PropertyMetadata(false, OnIsPlayingChanged));

    public static readonly DependencyProperty BarsForegroundProperty = DependencyProperty.Register(
        nameof(BarsForeground), typeof(Brush), typeof(PlayingSlot), new PropertyMetadata(null));

    public PlayingSlot()
    {
        Visibility = Visibility.Collapsed;
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>Warna batang; kosong berarti warna bawaan PlayingBars (merah).</summary>
    public Brush? BarsForeground
    {
        get => (Brush?)GetValue(BarsForegroundProperty);
        set => SetValue(BarsForegroundProperty, value);
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slot = (PlayingSlot)d;
        slot.Children.Clear();
        if (e.NewValue is true)
        {
            var bars = new PlayingBars { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (slot.BarsForeground is { } brush)
                bars.Foreground = brush;
            slot.Children.Add(bars);
        }
        slot.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed;
    }
}

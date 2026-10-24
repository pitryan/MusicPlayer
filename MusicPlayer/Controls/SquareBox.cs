using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MusicPlayer.Controls;

/// <summary>
/// Panel yang selalu persegi: tingginya sama dengan lebarnya. Untuk cover di grid yang lebar kotaknya mengikuti lebar
/// jendela (lihat TileGrid), supaya cover tetap persegi di ukuran berapa pun.
/// </summary>
public sealed class SquareBox : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double side = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        foreach (var child in Children)
            child.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double side = finalSize.Width;
        foreach (var child in Children)
            child.Arrange(new Rect(0, 0, side, side));
        return new Size(side, side);
    }
}

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicPlayer.Helpers;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Controls;

/// <summary>Rak rilis mendatar dengan panah (lihat XAML-nya).</summary>
public sealed partial class AlbumShelf : UserControl
{
    private IReadOnlyList<AlbumItem> _items = [];

    public AlbumShelf()
    {
        InitializeComponent();
        ShelfScroll.Attach(Scroller, Repeater, LeftArrow, RightArrow);
    }

    /// <summary>Kotak diklik: halaman pemakai rak yang membuka albumnya.</summary>
    public event EventHandler<AlbumItem>? ItemInvoked;

    public string Title
    {
        get => TitleBlock.Text;
        set => TitleBlock.Text = value;
    }

    /// <summary>Isi rak. Rak disembunyikan kalau kosong, dan kembali ke awal setiap isinya diganti.</summary>
    public IReadOnlyList<AlbumItem> Items
    {
        get => _items;
        set
        {
            _items = value;
            Repeater.ItemsSource = value;
            Root.Visibility = value.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in value)
                item.EnsureCover();
            Scroller.ChangeView(0, null, null, true);
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumItem item)
            ItemInvoked?.Invoke(this, item);
    }

    // Cover membesar sedikit saat kursor di atasnya, seperti di grid album
    private void Cover_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Cover_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);
}

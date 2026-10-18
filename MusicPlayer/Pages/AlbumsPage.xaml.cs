using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Grid album. Parameter navigasi "recent" menampilkan Recently Added; selain itu, Albums.</summary>
public sealed partial class AlbumsPage : Page
{
    private string? _mode;

    public AlbumsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        string mode = e.Parameter as string ?? "albums";

        // Kembali dari detail album: isi dan posisi scroll dipertahankan (halaman ini di-cache)
        if (e.NavigationMode == NavigationMode.Back && mode == _mode)
            return;

        _mode = mode;
        LoadAlbums();
    }

    private void LoadAlbums()
    {
        var albums = App.Database.GetAlbums().Select(a => new AlbumItem(a)).ToList();
        CountText.Text = $"{albums.Count} albums";
        EmptyState.Visibility = albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == "recent")
        {
            PageTitle.Text = "Recently Added";
            var source = new CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = AlbumGroup.ByDateAdded(albums, DateTime.UtcNow),
            };
            AlbumGrid.ItemsSource = source.View;
        }
        else
        {
            PageTitle.Text = "Albums";
            AlbumGrid.ItemsSource = albums;
        }
    }

    private void AlbumGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Dipanggil saat sebuah kotak album akan tampil: saat itulah cover-nya dimuat
        if (!args.InRecycleQueue && args.Item is AlbumItem album)
            album.EnsureCover();
    }

    private void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumItem album)
            Frame.Navigate(typeof(AlbumDetailPage), album.Summary);
    }
}

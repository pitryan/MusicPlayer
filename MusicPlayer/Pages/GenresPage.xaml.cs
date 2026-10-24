using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Daftar genre. Klik sebuah genre untuk melihat album-albumnya (lewat AlbumsPage).</summary>
public sealed partial class GenresPage : Page
{
    public GenresPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Kembali dari daftar album sebuah genre: isi dan posisi scroll dipertahankan
        if (e.NavigationMode == NavigationMode.Back && GenreGrid.ItemsSource is not null)
            return;

        var genres = App.Database.GetGenres().Select(g => new GenreItem(g)).ToList();
        GenreGrid.ItemsSource = genres;
        Header.Detail = Formatters.Count(genres.Count, "genre");
        EmptyState.Visibility = genres.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GenreGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is GenreItem genre)
            genre.EnsureCover();
    }

    private void GenreGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GenreItem genre)
            Frame.Navigate(typeof(AlbumsPage), "genre:" + genre.Summary.Name, Animations.DrillIn);
    }

    // Kotak membesar sedikit saat kursor di atasnya, seperti Apple Music
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);
}

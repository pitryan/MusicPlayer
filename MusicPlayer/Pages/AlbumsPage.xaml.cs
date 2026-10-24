using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>
/// Grid album. Parameter navigasi "recent" menampilkan Recently Added, "recentlyplayed" menampilkan Recently Played,
/// "genre:&lt;nama&gt;" menampilkan album dari satu genre, dan selain itu menampilkan semua album.
/// </summary>
public sealed partial class AlbumsPage : Page
{
    private string? _mode;
    private int _libraryVersion;

    public AlbumsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        string mode = e.Parameter as string ?? "albums";

        // Kembali dari detail album: isi dan posisi scroll dipertahankan (halaman ini di-cache), kecuali tag lagu
        // diedit selama di halaman lain
        if (e.NavigationMode == NavigationMode.Back && mode == _mode && _libraryVersion == App.TagEdits.Version)
            return;

        _mode = mode;
        _libraryVersion = App.TagEdits.Version;
        LoadAlbums();
    }

    private const string GenrePrefix = "genre:";

    /// <summary>Parameter navigasi untuk Recently Played.</summary>
    public const string RecentlyPlayedMode = "recentlyplayed";

    // Jumlah album di Recently Played; lebih dari ini jarang dicari lagi lewat halaman ini
    private const int RecentlyPlayedLimit = 60;

    private void LoadAlbums()
    {
        EmptyTitle.Text = "No albums yet";
        EmptyDetail.Text = "Add a music folder in Settings (the gear icon at the bottom left).";

        if (_mode == RecentlyPlayedMode)
        {
            LoadRecentlyPlayed();
            return;
        }

        string? genre = _mode is not null && _mode.StartsWith(GenrePrefix, StringComparison.Ordinal)
            ? _mode[GenrePrefix.Length..]
            : null;

        var albums = App.Database.GetAlbums(genre).Select(a => new AlbumItem(a)).ToList();
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
            PageTitle.Text = genre ?? "Albums";
            AlbumGrid.ItemsSource = albums;
        }
    }

    private void LoadRecentlyPlayed()
    {
        PageTitle.Text = "Recently Played";
        var recent = App.Database.GetRecentlyPlayedAlbums(RecentlyPlayedLimit)
            .Select(r => (Item: new AlbumItem(r.Album), r.LastPlayedUtc))
            .ToList();
        CountText.Text = $"{recent.Count} albums";

        EmptyTitle.Text = "Nothing played yet";
        EmptyDetail.Text = "Albums appear here after you've listened to a song from them.";
        EmptyState.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AlbumGrid.ItemsSource = new CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = AlbumGroup.ByLastPlayed(recent, DateTime.UtcNow),
        }.View;
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
            Frame.Navigate(typeof(AlbumDetailPage), album.Summary, Animations.DrillIn);
    }

    // Kotak membesar sedikit saat kursor di atasnya, seperti Apple Music
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

public sealed partial class AllPlaylistsPage : Page
{
    public AllPlaylistsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load();
        App.Playlists.Changed += Library_Changed;
        App.Favourites.Changed += Library_Changed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Playlists.Changed -= Library_Changed;
        App.Favourites.Changed -= Library_Changed;
    }

    private void Library_Changed(object? sender, EventArgs e) => Load();

    private void Load()
    {
        var playlists = App.Playlists.GetAll();
        var items = new List<PlaylistItem> { PlaylistItem.Favourites(App.Database.GetFavouriteTracks().Count) };
        items.AddRange(playlists.Select(p => new PlaylistItem(p)));
        PlaylistGrid.ItemsSource = items;
        HeaderCountText.Text = Formatters.Count(playlists.Count, "playlist");
    }

    private void PlaylistGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is PlaylistItem item)
            item.EnsureCovers();
    }

    private void PlaylistGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item)
            Frame.Navigate(typeof(PlaylistPage), item.Summary is { } summary ? summary.Id : PlaylistPage.Favourites, Animations.DrillIn);
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.AskNameAsync("New Playlist", "Create") is { } name)
            App.MainWindow.ShowPlaylist(App.Playlists.Create(name));
    }

    // Kotak membesar sedikit saat kursor di atasnya, seperti Apple Music
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Animations.Hover((UIElement)sender, false);
}

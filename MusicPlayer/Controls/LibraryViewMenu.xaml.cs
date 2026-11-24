using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.Controls;

/// <summary>Menu filter favorit dan urutan tampilan, diingat terpisah untuk setiap halaman.</summary>
public sealed partial class LibraryViewMenu : UserControl
{
    private string _key = "";
    private (BrowseSort Sort, string Label)[] _sorts = [];
    private bool _showFilter = true;
    public BrowseOptions Options { get; private set; } = new();
    public event EventHandler? Changed;

    public LibraryViewMenu() => InitializeComponent();

    public void Configure(string key, (BrowseSort Sort, string Label)[] sorts, bool showFilter = true)
    {
        _key = "LibraryView." + key;
        Visibility = Visibility.Visible;
        _sorts = sorts;
        _showFilter = showFilter;
        var sort = (BrowseSort)AppSettings.Get(_key + ".Sort", (int)sorts[0].Sort);
        if (!sorts.Any(s => s.Sort == sort)) sort = sorts[0].Sort;
        Options = new(sort, sort != BrowseSort.Default && AppSettings.Get(_key + ".Descending", false),
            showFilter && AppSettings.Get(_key + ".Favourites", false));
    }

    public static (BrowseSort, string)[] TrackSorts(string defaultLabel) =>
    [
        (BrowseSort.Default, defaultLabel), (BrowseSort.Title, "Title"), (BrowseSort.Artist, "Artist"),
        (BrowseSort.Album, "Album"), (BrowseSort.Duration, "Duration"),
        (BrowseSort.Genre, "Genre"), (BrowseSort.Quality, "Quality"),
    ];

    private void Change(BrowseOptions options)
    {
        Options = options;
        AppSettings.Set(_key + ".Sort", (int)options.Sort);
        AppSettings.Set(_key + ".Descending", options.Descending);
        AppSettings.Set(_key + ".Favourites", options.OnlyFavourites);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        if (_showFilter)
        {
            menu.Items.Add(Choice("All Items", !Options.OnlyFavourites, () => Change(Options with { OnlyFavourites = false })));
            menu.Items.Add(Choice("Only Favourites", Options.OnlyFavourites, () => Change(Options with { OnlyFavourites = true })));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var sortMenu = new MenuFlyoutSubItem { Text = "Sort By" };
        foreach (var (sort, label) in _sorts)
            sortMenu.Items.Add(Choice(label, Options.Sort == sort,
                () => Change(Options with { Sort = sort, Descending = sort != BrowseSort.Default && Options.Descending })));
        sortMenu.Items.Add(new MenuFlyoutSeparator());
        var ascending = Choice("Ascending", !Options.Descending, () => Change(Options with { Descending = false }));
        var descending = Choice("Descending", Options.Descending, () => Change(Options with { Descending = true }));
        ascending.IsEnabled = descending.IsEnabled = Options.Sort != BrowseSort.Default;
        sortMenu.Items.Add(ascending);
        sortMenu.Items.Add(descending);
        menu.Items.Add(sortMenu);
        menu.ShowAt(MenuButton);
    }

    private static ToggleMenuFlyoutItem Choice(string text, bool selected, Action action)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = selected };
        item.Click += (_, _) => action();
        return item;
    }
}

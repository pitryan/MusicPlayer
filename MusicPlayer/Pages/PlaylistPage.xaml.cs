using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace MusicPlayer.Pages;

/// <summary>
/// Halaman playlist. Parameter navigasinya <see cref="Favourites"/> untuk Favourite Songs, atau id playlist
/// (long) untuk playlist buatan sendiri.
/// </summary>
public sealed partial class PlaylistPage : Page
{
    /// <summary>Parameter navigasi untuk Favourite Songs.</summary>
    public const string Favourites = "favourites";

    private const int CoverPixelWidth = 440;  // dua kali ukuran tampilnya (220 px)

    private long? _playlistId;               // null = Favourite Songs
    private string _name = "";
    private ObservableCollection<TrackRow> _rows = [];
    private int _coverVersion;

    // Perubahan dari halaman ini sendiri (seret, hapus baris) sudah diterapkan langsung ke daftar; memuat ulang hanya
    // akan menggulir daftar kembali ke atas
    private bool _ignoreChanges;

    public PlaylistPage()
    {
        InitializeComponent();
    }

    private bool IsFavourites => _playlistId is null;

    private List<Track> Tracks => _rows.Select(r => r.Track).ToList();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _playlistId = e.Parameter is long id ? id : null;

        // Lagu playlist buatan sendiri bisa diurutkan ulang dengan diseret, seperti Apple Music
        TrackList.CanDragItems = !IsFavourites;
        TrackList.CanReorderItems = !IsFavourites;
        TrackList.AllowDrop = !IsFavourites;
        Load();

        App.Playback.PropertyChanged += Playback_PropertyChanged;
        App.Favourites.Changed += Favourites_Changed;
        App.Playlists.Changed += Playlists_Changed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Playback.PropertyChanged -= Playback_PropertyChanged;
        App.Favourites.Changed -= Favourites_Changed;
        App.Playlists.Changed -= Playlists_Changed;
    }

    private void Load()
    {
        IEnumerable<TrackRow> rows;
        if (_playlistId is long id)
        {
            _name = App.Playlists.Find(id)?.Name ?? "";
            rows = App.Playlists.GetEntries(id)
                .Select((entry, index) => new TrackRow(entry.Track, index) { PlaylistItemId = entry.ItemId });
        }
        else
        {
            _name = "Favourite Songs";
            rows = App.Database.GetFavouriteTracks().Select((track, index) => new TrackRow(track, index));
        }

        _rows = new ObservableCollection<TrackRow>(rows);
        TrackList.ItemsSource = _rows;
        TitleText.Text = _name;
        UpdateHeader();
        MarkNowPlaying();
    }

    /// <summary>Jumlah lagu, durasi, cover, dan pesan kosong; dipanggil setiap kali isi daftar berubah.</summary>
    private void UpdateHeader()
    {
        var total = TimeSpan.FromTicks(_rows.Sum(r => r.Track.Duration.Ticks));
        bool empty = _rows.Count == 0;
        MetaText.Text = empty ? "" : $"{Formatters.Count(_rows.Count, "song")} · {Formatters.LongDuration(total)}";
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeaders.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.IsEnabled = !empty;
        ShuffleButton.IsEnabled = !empty;
        EmptyTitle.Text = IsFavourites ? "No favourite songs yet" : "This playlist is empty";
        EmptyDetail.Text = IsFavourites
            ? "Click the star in the player at the top, or choose Favourite from a song's menu."
            : "Add songs with Add to Playlist in a song's or an album's menu.";
        _ = LoadCoverAsync();
    }

    private async Task LoadCoverAsync()
    {
        int version = ++_coverVersion;
        FavouritesTile.Visibility = IsFavourites ? Visibility.Visible : Visibility.Collapsed;

        // Cover dari album yang berbeda, dalam urutan playlist: 4 atau lebih jadi mozaik, kurang dari itu satu cover
        var paths = new List<string>();
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _rows.Select(r => r.Track))
        {
            if (paths.Count == 4)
                break;
            if (albums.Add($"{track.AlbumArtist ?? track.Artist}|{track.Album}"))
                paths.Add(track.FilePath);
        }

        bool mosaic = !IsFavourites && paths.Count >= 4;
        bool single = !IsFavourites && paths.Count is >= 1 and < 4;
        MosaicCover.Visibility = mosaic ? Visibility.Visible : Visibility.Collapsed;
        SingleCover.Visibility = single ? Visibility.Visible : Visibility.Collapsed;

        if (single)
        {
            var image = await CoverCache.GetAsync(paths[0], CoverPixelWidth);
            if (version == _coverVersion)
                SingleCoverBrush.ImageSource = image;
        }
        else if (mosaic)
        {
            var brushes = new[] { Mosaic1, Mosaic2, Mosaic3, Mosaic4 };
            for (int i = 0; i < 4; i++)
            {
                var image = await CoverCache.GetAsync(paths[i], CoverPixelWidth / 2);
                if (version != _coverVersion)
                    return; // isi playlist sudah berubah lagi; cover yang lebih baru sedang dimuat
                brushes[i].ImageSource = image;
            }
        }
    }

    private void Favourites_Changed(object? sender, EventArgs e)
    {
        // Favourite Songs: daftar dimuat ulang, jadi lagu yang baru difavoritkan langsung muncul paling atas.
        // Playlist lain: cukup bintang di barisnya.
        if (IsFavourites)
            Load();
        else
            TrackRow.RefreshFavourites(_rows);
    }

    private void Playlists_Changed(object? sender, EventArgs e)
    {
        if (!IsFavourites && !_ignoreChanges)
            Load();
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.CurrentTrack))
            MarkNowPlaying();
    }

    private void MarkNowPlaying()
    {
        string? playingPath = App.Playback.CurrentTrack?.FilePath;
        foreach (var row in _rows)
            row.IsPlaying = row.Track.FilePath == playingPath;
    }

    private void Restripe()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].SetIndex(i);
    }

    private void TrackList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_playlistId is not long id || args.DropResult != DataPackageOperation.Move)
            return;

        var order = _rows.Select(r => r.PlaylistItemId!.Value).ToList();
        _ignoreChanges = true;
        try
        {
            App.Playlists.Reorder(id, order);
            Restripe();
            UpdateHeader(); // urutan album berubah, jadi cover mozaik bisa ikut berubah
        }
        catch (ArgumentException)
        {
            Load(); // isi playlist sempat berubah di tempat lain: tampilkan isinya yang sekarang
        }
        finally
        {
            _ignoreChanges = false;
        }
    }

    /// <summary>"Remove from Playlist" di menu baris: hanya baris itu, walaupun lagu yang sama ada dua kali.</summary>
    private void RemoveRow(TrackRow row)
    {
        if (_playlistId is not long id || row.PlaylistItemId is not long itemId)
            return;

        _ignoreChanges = true;
        try
        {
            App.Playlists.Remove(id, [itemId]);
        }
        finally
        {
            _ignoreChanges = false;
        }

        _rows.Remove(row);
        Restripe();
        UpdateHeader();
    }

    private void ShowRowMenu(UIElement target, Point position, TrackRow row)
    {
        int index = _rows.IndexOf(row);
        TrackMenu.Show(target, position, Tracks, index,
            removeFromPlaylist: IsFavourites ? null : () => RemoveRow(row));
    }

    private void PlaylistMore_Click(object sender, RoutedEventArgs e)
    {
        var tracks = Tracks;
        var menu = new MenuFlyout();

        // Play Next dari lagu terakhir ke pertama: setiap lagu masuk ke depan antrean, jadi urutan playlist terjaga
        var playNext = TrackMenu.CreateItem("Play Next", "\uE893", () =>
        {
            for (int i = tracks.Count - 1; i >= 0; i--)
                App.Playback.PlayNext(tracks[i]);
        });
        var addToQueue = TrackMenu.CreateItem("Add to Queue", "\uE710", () =>
        {
            foreach (var track in tracks)
                App.Playback.AddToQueue(track);
        });
        playNext.IsEnabled = addToQueue.IsEnabled = tracks.Count > 0;
        menu.Items.Add(playNext);
        menu.Items.Add(addToQueue);

        if (_playlistId is long id)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(TrackMenu.CreateItem("Rename…", "\uE8AC", () => _ = RenameAsync(id)));
            menu.Items.Add(TrackMenu.CreateItem("Delete Playlist…", "\uE74D", () => _ = DeleteAsync(id)));
        }
        menu.ShowAt(PlaylistMoreButton, new Point(0, PlaylistMoreButton.ActualHeight));
    }

    private async Task RenameAsync(long id)
    {
        if (await Dialogs.AskNameAsync("Rename Playlist", "Rename", _name) is { } name)
            App.Playlists.Rename(id, name);
    }

    private async Task DeleteAsync(long id)
    {
        bool confirmed = await Dialogs.ConfirmAsync(
            $"Delete \"{_name}\"?", "The playlist will be deleted. Its songs stay in your library.", "Delete");
        if (!confirmed)
            return;

        _ignoreChanges = true;
        App.Playlists.Delete(id);
        App.MainWindow.ShowSection(MainWindow.AllPlaylistsTag);
    }

    private void TrackList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Cover kecil baru dimuat saat barisnya akan tampil, supaya daftar panjang tetap ringan
        if (!args.InRecycleQueue && args.Item is TrackRow row)
            row.EnsureCover();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count > 0)
            App.Playback.PlayQueue(Tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e) => App.Playback.PlayShuffled(Tracks);

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackRow row } button)
            ShowRowMenu(button, new Point(0, button.ActualHeight), row);
    }

    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
            ShowRowMenu(TrackList, e.GetPosition(TrackList), row);
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan seluruh playlist ini sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
        {
            int index = _rows.IndexOf(row);
            if (index >= 0)
                App.Playback.PlayQueue(Tracks, index);
        }
    }
}

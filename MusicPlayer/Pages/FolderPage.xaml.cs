using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>
/// Isi sebuah folder: subfolder-subfolder dan lagu yang ada langsung di folder itu.
/// Parameter navigasinya adalah path folder.
/// </summary>
public sealed partial class FolderPage : Page
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private string _folder = "";
    private List<Track> _tracks = [];
    private List<TrackRow> _rows = [];

    public FolderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not string folder)
            return;

        _folder = LibraryPaths.WithoutTrailingSeparator(folder);
        FolderNameText.Text = LibraryPaths.DisplayName(_folder);
        PathBar.ItemsSource = BuildCrumbs(_folder);

        var subfolders = App.Database.GetSubfolders(_folder).Select(f => new FolderItem(f)).ToList();
        SubfolderGrid.ItemsSource = subfolders;
        SubfolderGrid.Visibility = subfolders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var subfolder in subfolders)
            subfolder.EnsureCover();

        _tracks = App.Database.GetFolderTracks(_folder, includeSubfolders: false).ToList();
        _rows = _tracks.Select((track, index) => new TrackRow(track, index)).ToList();
        TrackList.ItemsSource = _rows;
        SongsHeader.Visibility = _tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        int totalSongs = App.Database.CountTracks(_folder);
        FolderCountText.Text =
            $"{Formatters.Count(subfolders.Count, "folder")}, {Formatters.Count(totalSongs, "song")}";

        App.Playback.PropertyChanged += Playback_PropertyChanged;
        MarkNowPlaying();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Playback.PropertyChanged -= Playback_PropertyChanged;
    }

    /// <summary>Jalur dari folder library sampai folder ini, misalnya "music" › "Japan" › "YOASOBI".</summary>
    private static List<FolderCrumb> BuildCrumbs(string folder)
    {
        string? libraryFolder = App.Database.GetFolders().FirstOrDefault(f => LibraryPaths.IsUnder(folder, f));
        if (libraryFolder is null)
            return [new FolderCrumb(LibraryPaths.DisplayName(folder), folder)];

        string current = LibraryPaths.WithoutTrailingSeparator(libraryFolder);
        var crumbs = new List<FolderCrumb> { new(LibraryPaths.DisplayName(current), current) };

        string relative = folder.Length > current.Length ? folder[current.Length..] : "";
        foreach (string part in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            crumbs.Add(new FolderCrumb(part, current));
        }
        return crumbs;
    }

    private void PathBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is FolderCrumb crumb && !string.Equals(crumb.Path, _folder, StringComparison.OrdinalIgnoreCase))
            Frame.Navigate(typeof(FolderPage), crumb.Path);
    }

    private void SubfolderGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderItem subfolder)
            Frame.Navigate(typeof(FolderPage), subfolder.Summary.Path);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        // Putar semua lagu di folder ini, termasuk yang ada di subfolder, urut path
        var tracks = App.Database.GetFolderTracks(_folder, includeSubfolders: true);
        if (tracks.Count > 0)
            App.Playback.PlayQueue(tracks, 0);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        var tracks = App.Database.GetFolderTracks(_folder, includeSubfolders: true)
            .OrderBy(_ => Random.Shared.Next())
            .ToList();
        if (tracks.Count > 0)
            App.Playback.PlayQueue(tracks, 0);
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
        {
            int index = _rows.IndexOf(row);
            if (index >= 0)
                App.Playback.PlayQueue(_tracks, index);
        }
    }

    private void Playback_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.PlaybackService.CurrentTrack))
            MarkNowPlaying();
    }

    private void MarkNowPlaying()
    {
        string? playingPath = App.Playback.CurrentTrack?.FilePath;
        foreach (var row in _rows)
            row.IsPlaying = row.Track.FilePath == playingPath;
    }
}

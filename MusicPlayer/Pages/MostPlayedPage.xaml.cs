using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Foundation;

namespace MusicPlayer.Pages;

/// <summary>Lagu yang paling sering diputar, sepanjang waktu atau 30 hari terakhir.</summary>
public sealed partial class MostPlayedPage : Page
{
    private const int Limit = 100;
    private static readonly TimeSpan LastMonth = TimeSpan.FromDays(30);

    private List<Track> _tracks = [];
    private List<TrackRow> _rows = [];

    // SelectorBar sudah memicu SelectionChanged saat XAML-nya dibangun, sebelum halaman siap
    private bool _ready;

    public MostPlayedPage()
    {
        InitializeComponent();
    }

    private bool IsLastMonth => RangeTabs.SelectedItem == LastMonthTab;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = true;
        Load();
        App.Playback.PropertyChanged += Playback_PropertyChanged;
        App.Playback.PlayRecorded += Playback_PlayRecorded;
        App.Favourites.Changed += Favourites_Changed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Playback.PropertyChanged -= Playback_PropertyChanged;
        App.Playback.PlayRecorded -= Playback_PlayRecorded;
        App.Favourites.Changed -= Favourites_Changed;
    }

    private void Load()
    {
        var counts = App.Database.GetMostPlayed(Limit, IsLastMonth ? DateTime.UtcNow - LastMonth : null);
        _tracks = counts.Select(c => c.Track).ToList();
        _rows = counts.Select((c, index) => new TrackRow(c.Track, index)
        {
            RankText = (index + 1).ToString(CultureInfo.InvariantCulture),
            PlaysText = c.Count.ToString(CultureInfo.InvariantCulture),
        }).ToList();
        TrackList.ItemsSource = _rows;

        Header.Detail = Formatters.Count(_rows.Count, "song");
        bool empty = _rows.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeaders.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = IsLastMonth ? "Nothing played in the last 30 days" : "Nothing played yet";
        MarkNowPlaying();
    }

    private void RangeTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_ready)
            Load();
    }

    // Sebuah lagu baru saja tercatat di History: peringkat dan jumlah putar ikut diperbarui
    private void Playback_PlayRecorded(object? sender, EventArgs e) => Load();

    private void Favourites_Changed(object? sender, EventArgs e) => TrackRow.RefreshFavourites(_rows);

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

    private void TrackList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is TrackRow row)
            row.EnsureCover();
    }

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackRow row } button)
            TrackMenu.Show(button, new Point(0, button.ActualHeight), _tracks, _rows.IndexOf(row));
    }

    private void TrackList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
            TrackMenu.Show(TrackList, e.GetPosition(TrackList), _tracks, _rows.IndexOf(row));
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan seluruh daftar peringkat ini sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TrackRow row)
        {
            int index = _rows.IndexOf(row);
            if (index >= 0)
                App.Playback.PlayQueue(_tracks, index);
        }
    }
}

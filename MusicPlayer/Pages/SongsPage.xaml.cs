using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.Pages;

public sealed partial class SongsPage : Page
{
    private List<Track> _tracks = [];

    public SongsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _tracks = App.Database.GetTracks().ToList();
        SongsList.ItemsSource = _tracks;
        CountText.Text = $"{_tracks.Count} songs";
        EmptyState.Visibility = _tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SongsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
            TrackMenu.Show(SongsList, e.GetPosition(SongsList), _tracks, _tracks.IndexOf(track));
    }

    private void SongsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Klik dua kali: putar lagu itu, dengan seluruh daftar ini sebagai antrean
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
        {
            int index = _tracks.IndexOf(track);
            if (index >= 0)
                App.Playback.PlayQueue(_tracks, index);
        }
    }
}

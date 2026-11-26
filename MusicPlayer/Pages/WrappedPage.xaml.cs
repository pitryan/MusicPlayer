using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Wrapped: ringkasan setahun mendengarkan (lihat <see cref="Wrapped"/>).</summary>
public sealed partial class WrappedPage : Page
{
    private const int ShownSongs = 10;
    private const int ShownArtists = 6;
    private const int ShownAlbums = 6;

    // Tanggal di kartu: "20 Oct", sama dengan bahasa tampilan aplikasi
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    private IReadOnlyList<PlayRecord> _plays = [];
    private WrappedReport? _report;
    private bool _ready;

    public WrappedPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _plays = await Task.Run(App.Database.GetAllPlays);
        var years = Wrapped.Years(_plays, TimeZoneInfo.Local);
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        if (years.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        ContentScroller.Visibility = Visibility.Visible;
        YearBox.ItemsSource = years;
        YearBox.Visibility = years.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _ready = true;
        YearBox.SelectedIndex = 0; // tahun terbaru
    }

    private async void YearBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || YearBox.SelectedItem is not int year)
            return;
        var plays = _plays;
        var report = await Task.Run(() => Wrapped.Build(plays, year, TimeZoneInfo.Local));
        if (YearBox.SelectedItem is int selected && selected == year)
            Show(report);
    }

    private void Show(WrappedReport report)
    {
        _report = report;
        KickerText.Text = $"YOUR {report.Year} IN MUSIC";
        MinutesText.Text = ((long)report.ListeningTime.TotalMinutes).ToString("N0");
        CountsText.Text = $"{Formatters.Count(report.Plays, "play")} · {Formatters.Count(report.Songs, "song")} · "
                          + Formatters.Count(report.Artists, "artist");
        SaveText.Text = "Save as Playlist";
        SaveButton.IsEnabled = report.TopSongs.Count > 0;
        PlayTopButton.IsEnabled = report.TopSongs.Count > 0;

        ShowTopSong(report);
        ShowTopArtist(report);

        SongsList.ItemsSource = report.TopSongs.Take(ShownSongs).Select((s, i) => new WrappedItem(i + 1, s.Track.DisplayTitle,
            s.Track.DisplayArtist, Plays(s.Plays), s.Track.FilePath, s)).ToList();
        ArtistsList.ItemsSource = report.TopArtists.Take(ShownArtists).Select((a, i) => new WrappedItem(i + 1, a.DisplayName,
            "", Plays(a.Plays), a.CoverTrackPath, a)).ToList();
        AlbumsGrid.ItemsSource = report.TopAlbums.Take(ShownAlbums).Select((a, i) => new WrappedItem(i + 1, a.Title,
            $"{(a.Artist.Length > 0 ? a.Artist : "Unknown Artist")} · {Plays(a.Plays)}", "", a.CoverTrackPath, a, coverWidth: 320)).ToList();

        ShowGenres(report);
        ShowListeningTimes(report);
        ShowQuality(report);
        ShowFacts(report);
        ContentScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static string Plays(int count) => Formatters.Count(count, "play");

    private async void ShowTopSong(WrappedReport report)
    {
        TopSongCover.Source = null;
        if (report.TopSongs.FirstOrDefault() is not { } top)
            return;
        TopSongTitle.Text = top.Track.DisplayTitle;
        TopSongArtist.Text = top.Track.DisplayArtist;
        TopSongPlays.Text = $"Played {Times(top.Plays)}";
        var cover = await CoverCache.GetAsync(top.Track.FilePath, 280);
        if (ReferenceEquals(_report, report))
            TopSongCover.Source = cover;
    }

    private async void ShowTopArtist(WrappedReport report)
    {
        TopArtistCover.ImageSource = null;
        if (report.TopArtists.FirstOrDefault() is not { } top)
            return;
        TopArtistName.Text = top.DisplayName;
        TopArtistPlays.Text = $"{Plays(top.Plays)} · {Percent((double)top.Plays / report.Plays)} of your listening";
        var cover = await CoverCache.GetAsync(top.CoverTrackPath, 280);
        if (ReferenceEquals(_report, report))
            TopArtistCover.ImageSource = cover;
    }

    private static string Times(int count) => count == 1 ? "once" : count == 2 ? "twice" : $"{count} times";

    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";

    // ── Genre, waktu, dan kualitas ───────────────────────────

    private void ShowGenres(WrappedReport report)
    {
        GenresPanel.Children.Clear();
        if (report.TopGenres.Count == 0)
        {
            GenresPanel.Children.Add(Secondary("Your songs don't have genres yet."));
            return;
        }
        foreach (var genre in report.TopGenres.Take(5))
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = genre.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var share = Secondary(Percent(genre.Share));
            Grid.SetColumn(share, 1);
            row.Children.Add(share);

            var bar = new ProgressBar { Value = genre.Share * 100, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) };
            var item = new StackPanel();
            item.Children.Add(row);
            item.Children.Add(bar);
            GenresPanel.Children.Add(item);
        }
    }

    /// <summary>Batang per bulan, dan bagian hari yang paling sering dipakai mendengarkan.</summary>
    private void ShowListeningTimes(WrappedReport report)
    {
        DayPartText.Text = report.FavouriteDayPart switch
        {
            DayPart.Morning => "Mostly in the morning",
            DayPart.Afternoon => "Mostly in the afternoon",
            DayPart.Evening => "Mostly in the evening",
            DayPart.Night => "Mostly late at night",
            _ => "",
        };

        MonthsChart.Children.Clear();
        MonthsChart.ColumnDefinitions.Clear();
        MonthsChart.RowDefinitions.Clear();
        MonthsChart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        MonthsChart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int max = Math.Max(1, report.PlaysByMonth.Max());
        var accent = (Brush)Application.Current.Resources["AccentRedBrush"];
        var empty = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        for (int month = 0; month < 12; month++)
        {
            MonthsChart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int plays = report.PlaysByMonth[month];
            var bar = new Rectangle
            {
                Height = plays == 0 ? 3 : Math.Max(4, 84.0 * plays / max),
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 2,
                RadiusY = 2,
                Fill = plays == 0 ? empty : accent,
            };
            string name = English.DateTimeFormat.GetAbbreviatedMonthName(month + 1);
            ToolTipService.SetToolTip(bar, $"{name}: {Plays(plays)}");
            Grid.SetColumn(bar, month);
            MonthsChart.Children.Add(bar);

            var label = Secondary(name[..1]);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetColumn(label, month);
            Grid.SetRow(label, 1);
            MonthsChart.Children.Add(label);
        }
    }

    /// <summary>Hi-Res, lossless, dan lossy sebagai satu batang bertingkat.</summary>
    private void ShowQuality(WrappedReport report)
    {
        int hiRes = report.PlaysByQuality.GetValueOrDefault(QualityKind.HiRes);
        int lossless = report.PlaysByQuality.GetValueOrDefault(QualityKind.Lossless);
        int lossy = report.PlaysByQuality.GetValueOrDefault(QualityKind.Lossy);
        double total = Math.Max(1, report.Plays);
        int losslessOrBetter = hiRes + lossless;
        QualityText.Text = hiRes >= lossless && hiRes > 0
            ? $"{Percent(hiRes / total)} of your listening was Hi-Res"
            : $"{Percent(losslessOrBetter / total)} of your listening was lossless";

        QualityBar.Children.Clear();
        QualityBar.ColumnDefinitions.Clear();
        (int Count, Windows.UI.Color Color)[] parts =
        [
            (hiRes, Windows.UI.Color.FromArgb(0xFF, 0xFA, 0x2D, 0x48)),
            (lossless, Windows.UI.Color.FromArgb(0xFF, 0xB0, 0x5C, 0xE8)),
            (lossy, Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        ];
        foreach (var (count, color) in parts.Where(p => p.Count > 0))
        {
            QualityBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
            var segment = new Rectangle { Fill = new SolidColorBrush(color) };
            Grid.SetColumn(segment, QualityBar.ColumnDefinitions.Count - 1);
            QualityBar.Children.Add(segment);
        }
        QualityLegend.Text = $"Hi-Res {Percent(hiRes / total)} · Lossless {Percent(lossless / total)} · Lossy {Percent(lossy / total)}";
    }

    // ── Fakta kecil ──────────────────────────────────────────

    private void ShowFacts(WrappedReport report)
    {
        var facts = new List<(string Label, string Value, string Detail)>();
        if (report.FirstPlay is { } first)
        {
            var day = TimeZoneInfo.ConvertTimeFromUtc(first.PlayedUtc, TimeZoneInfo.Local);
            facts.Add(($"FIRST SONG OF {report.Year}", first.Track.DisplayTitle,
                $"{first.Track.DisplayArtist} · {day.ToString("d MMM", English)}"));
        }
        if (report.BusiestDay is { } busiest)
            facts.Add(("BUSIEST DAY", busiest.ToString("d MMMM", English), $"{Plays(report.BusiestDayPlays)} that day"));
        facts.Add(("LONGEST STREAK", report.LongestStreak == 1 ? "1 day" : $"{report.LongestStreak} days in a row",
            $"You listened on {Formatters.Count(report.DaysListened, "day")}"));
        if (report.OnRepeat is { } repeat)
            facts.Add(("ON REPEAT", repeat.Track.DisplayTitle,
                $"Played {Times(repeat.Plays)} on {repeat.Day.ToString("d MMM", English)}"));

        FactsGrid.Children.Clear();
        for (int i = 0; i < facts.Count; i++)
        {
            var (label, value, detail) = facts[i];
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = label, Style = (Style)Resources["WrappedLabelStyle"] });
            panel.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var detailText = Secondary(detail);
            detailText.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(detailText);

            var card = new Border { Style = (Style)Resources["WrappedCardStyle"], Child = panel };
            Grid.SetColumn(card, i);
            FactsGrid.Children.Add(card);
        }
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
    };

    // ── Aksi ─────────────────────────────────────────────────

    private IReadOnlyList<Track> TopTracks => _report?.TopSongs.Select(s => s.Track).ToList() ?? [];

    private void PlayTop_Click(object sender, RoutedEventArgs e)
    {
        if (TopTracks is { Count: > 0 } tracks)
            App.Playback.PlayQueue(tracks, 0);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not { } report || TopTracks is not { Count: > 0 } tracks)
            return;
        string? name = await Dialogs.AskNameAsync("Save Top Songs", "Save", $"Top Songs {report.Year}");
        if (name is null)
            return;
        App.Playlists.CreateWith(name, tracks);
        SaveText.Text = "Saved";
        SaveButton.IsEnabled = false;
    }

    private void TopSong_Click(object sender, RoutedEventArgs e) => PlayTop_Click(sender, e);

    private void TopArtist_Click(object sender, RoutedEventArgs e)
    {
        if (_report?.TopArtists.FirstOrDefault() is { } top)
            Frame.Navigate(typeof(ArtistPage), top.Name, Animations.DrillIn);
    }

    /// <summary>Lagu yang diklik diputar, dengan peringkat lagu setahun ini sebagai antrean.</summary>
    private void SongsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WrappedItem { Target: RankedTrack song } && _report is { } report)
            App.Playback.PlayQueue(TopTracks, report.TopSongs.ToList().IndexOf(song));
    }

    private void ArtistsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WrappedItem { Target: RankedArtist artist })
            Frame.Navigate(typeof(ArtistPage), artist.Name, Animations.DrillIn);
    }

    /// <summary>Album dibuka lewat datanya di library, dengan kunci yang sama seperti halaman Albums.</summary>
    private void AlbumsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WrappedItem { Target: RankedAlbum album })
            return;
        var summary = App.Database.GetAlbums().FirstOrDefault(a =>
            string.Equals(a.Title, album.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Artist, album.Artist, StringComparison.OrdinalIgnoreCase));
        if (summary is not null)
            Frame.Navigate(typeof(AlbumDetailPage), summary, Animations.DrillIn);
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is WrappedItem item)
            item.EnsureCover();
    }
}

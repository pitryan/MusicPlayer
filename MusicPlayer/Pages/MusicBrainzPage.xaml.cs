using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Lagu yang dicari di MusicBrainz, petunjuk pencarian dari tag-nya, dan apakah editornya dibuka dari halaman album.</summary>
public sealed record MusicBrainzRequest(IReadOnlyList<string> Paths, string Album, string Artist, bool FromAlbum);

/// <summary>Mencari rilis di MusicBrainz, mencocokkan file dengan lagu-lagunya, lalu menulis tag-nya seperti Picard.</summary>
public sealed partial class MusicBrainzPage : Page
{
    private const string EnglishNamesSetting = "MusicBrainzEnglishNames";
    private const int PreviewCoverSize = 500;
    private const int SavedCoverSize = 1200; // cukup tajam untuk layar penuh, tanpa membuat file jauh lebih besar

    private MusicBrainzRequest _request = new([], "", "", false);
    private List<MatchFile> _files = [];
    private MbRelease? _release;
    private IReadOnlyList<TrackMatch> _matches = [];
    private CancellationTokenSource? _cts;
    private bool _saving;

    public MusicBrainzPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MusicBrainzRequest request)
            return;

        _request = request;
        SubtitleText.Text = Formatters.Count(request.Paths.Count, "song");
        AlbumBox.Text = MusicBrainzTagger.SearchTitle(request.Album);
        ArtistBox.Text = request.Artist;
        EnglishNamesBox.IsChecked = AppSettings.Get(EnglishNamesSetting, true);

        // Nomor, judul, dan durasi setiap file, untuk mencocokkannya dengan lagu di rilis
        _files = await Task.Run(() => request.Paths.Select(path =>
        {
            try
            {
                var values = TagEditor.Read(path);
                return new MatchFile(path, Number(values.Get("discnumber")), Number(values.Get("tracknumber")), values.Get("title"), values.Duration);
            }
            catch (Exception)
            {
                return new MatchFile(path, null, null, Path.GetFileNameWithoutExtension(path), TimeSpan.Zero);
            }
        }).ToList());

        if (AlbumBox.Text.Length > 0)
            await SearchAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
    }

    private static int? Number(string text) => int.TryParse(text, out int number) && number > 0 ? number : null;

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await SearchAsync();
    }

    private CancellationToken Restart()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private async Task SearchAsync()
    {
        string album = AlbumBox.Text.Trim();
        if (album.Length == 0)
        {
            ShowMessage("Type an album title to search for.");
            return;
        }

        var token = Restart();
        SetBusy(true);
        ShowMessage(null);
        PreviewPanel.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = false;
        try
        {
            var results = await App.MusicBrainz.SearchReleasesAsync(album, ArtistBox.Text.Trim(), token);
            // Paling cocok dulu; skor sama: yang jumlah lagunya paling dekat dengan jumlah file
            ResultsList.ItemsSource = results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => Math.Abs(r.TrackCount - _files.Count))
                .Select(r => new MbResultItem(r))
                .ToList();
            if (results.Count == 0)
                ShowMessage("No releases found. Try a shorter album title, or leave Artist empty.");
            else
                ShowMessage("Choose the release that matches your files: same number of tracks, format, and country.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ShowMessage($"Couldn't reach MusicBrainz ({ex.Message}). Check your internet connection, then search again.");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                SetBusy(false);
        }
    }

    private async void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not MbResultItem item)
            return;

        var token = Restart();
        SetBusy(true);
        SaveButton.IsEnabled = false;
        try
        {
            var release = await App.MusicBrainz.GetReleaseAsync(item.Release.Id, token);
            _release = release;
            _matches = MusicBrainzTagger.Match(_files, release);
            ShowPreview(release);

            ReleaseCover.Source = null;
            var cover = release.HasFrontCover ? await App.MusicBrainz.GetFrontCoverAsync(release.Id, PreviewCoverSize, token) : null;
            if (cover is not null)
                ReleaseCover.Source = await CoverImageLoader.FromBytesAsync(cover, 280);
            CoverArtBox.IsEnabled = cover is not null;
            CoverArtBox.IsChecked = cover is not null;
            SaveButton.IsEnabled = _matches.Any(m => m.Track is not null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ShowMessage($"Couldn't load this release from MusicBrainz ({ex.Message}). Try again in a moment.");
        }
        finally
        {
            if (!token.IsCancellationRequested)
                SetBusy(false);
        }
    }

    private void ShowPreview(MbRelease release)
    {
        PreviewPanel.Visibility = Visibility.Visible;
        ReleaseTitleText.Text = release.Title;
        ReleaseArtistText.Text = MusicBrainzTagger.CreditText(release.Credits, preferEnglishNames: false, sort: false);
        ReleaseDetailsText.Text = new MbResultItem(release).DetailsText;

        int matched = _matches.Count(m => m.Track is not null);
        MatchSummaryText.Text = matched == _files.Count
            ? $"All {Formatters.Count(matched, "song")} matched"
            : $"{matched} of {Formatters.Count(_files.Count, "song")} matched";
        MatchList.ItemsSource = _matches.Select(m => new MbMatchRow(
            Path.GetFileName(m.Path),
            m.Track is { } track
                ? $"{m.Medium!.Position}.{track.Position}  {track.Title}{(track.LengthMs is { } ms ? $" ({Formatters.Time(TimeSpan.FromMilliseconds(ms))})" : "")}"
                : "No match – this song won't be changed",
            m.Track is not null)).ToList();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Leave(saved: null);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _release is not { } release)
            return;

        bool english = EnglishNamesBox.IsChecked == true;
        AppSettings.Set(EnglishNamesSetting, english);
        var edits = _matches
            .Where(m => m.Track is not null)
            .Select(m => (m.Path, (IReadOnlyDictionary<string, string>)MusicBrainzTagger.BuildTags(release, m.Medium!, m.Track!, english)))
            .ToList();

        _saving = true;
        SetBusy(true);
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        try
        {
            IReadOnlyList<TagPicture>? pictures = null;
            if (CoverArtBox.IsChecked == true
                && await App.MusicBrainz.GetFrontCoverAsync(release.Id, SavedCoverSize, CancellationToken.None) is { } cover)
                pictures = [new TagPicture(TagLib.PictureType.FrontCover, "image/jpeg", cover)];

            var result = await App.TagEdits.SaveEachAsync(edits, pictures);
            if (result.Failed.Count == 0)
            {
                Leave(result.Saved);
                return;
            }

            ShowMessage($"Couldn't save {result.Failed.Count} of {edits.Count} songs: "
                + string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})")));
        }
        catch (Exception ex)
        {
            ShowMessage($"Couldn't save: {ex.Message}");
        }
        finally
        {
            _saving = false;
            SetBusy(false);
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Setelah menyimpan, editor tag di belakang halaman ini sudah usang, jadi ikut dilewati: kembali ke album (dengan
    /// data barunya) kalau dibuka dari halaman album, atau ke halaman sebelum editor. Cancel kembali ke editor.
    /// </summary>
    private void Leave(IReadOnlyList<Track>? saved)
    {
        if (saved is null)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            return;
        }

        var history = Frame.BackStack;
        if (_request.FromAlbum && saved.Count > 0 && App.Database.FindAlbum(saved[0]) is { } album)
        {
            Frame.Navigate(typeof(AlbumDetailPage), album);
            while (history.Count > 0 && (history[^1].SourcePageType == typeof(MusicBrainzPage) || history[^1].SourcePageType == typeof(TagEditorPage)))
                history.RemoveAt(history.Count - 1);
            if (history.Count > 0 && history[^1].SourcePageType == typeof(AlbumDetailPage))
                history.RemoveAt(history.Count - 1);
            return;
        }

        if (history.Count > 0 && history[^1].SourcePageType == typeof(TagEditorPage))
            history.RemoveAt(history.Count - 1);
        if (Frame.CanGoBack)
            Frame.GoBack();
        else
            Frame.Navigate(typeof(SongsPage));
    }

    private void ResultsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is MbResultItem item)
            item.EnsureCover();
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.IsEnabled = !busy;
    }

    private void ShowMessage(string? text)
    {
        MessageText.Text = text ?? "";
        MessageText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

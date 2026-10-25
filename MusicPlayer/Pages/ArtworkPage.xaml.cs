using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MusicPlayer.Pages;

/// <summary>Lagu yang covernya diganti; FromAlbum untuk judul halamannya (nama album, bukan judul lagu).</summary>
public sealed record ArtworkRequest(IReadOnlyList<Track> Tracks, bool FromAlbum);

/// <summary>Change Artwork: ganti cover saja, dari file atau Cover Art Archive (lihat XAML-nya).</summary>
public sealed partial class ArtworkPage : Page
{
    private const int PreviewPixels = 640;
    // Cover yang ditanam: ukuran terbesar yang umum di Cover Art Archive; rilis lama kadang hanya punya 500
    private const int EmbedSize = 1200;
    private const int FallbackSize = 500;
    // Thumbnail dimuat beberapa sekaligus, supaya grid cepat terisi tanpa membanjiri Cover Art Archive
    private const int ParallelThumbnails = 4;

    private readonly ObservableCollection<ArtworkItem> _results = [];
    private ArtworkRequest? _request;
    // Cover yang dipilih (asli), dan yang akan ditanam (asli, atau sudah dibuat persegi sesuai pilihan)
    private TagPicture? _sourceCover;
    private TagPicture? _newCover;
    private int _shapeVersion;
    private bool _settingShape;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;
    private bool _saving;

    public ArtworkPage()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _request = e.Parameter as ArtworkRequest;
        if (_request is not { Tracks.Count: > 0 } request)
            return;

        var first = request.Tracks[0];
        string albumArtist = first.AlbumArtist ?? first.DisplayArtist;
        TitleText.Text = request.FromAlbum ? first.DisplayAlbum : first.DisplayTitle;
        TargetText.Text = request.FromAlbum
            ? $"{albumArtist} · {Formatters.Count(request.Tracks.Count, "song")}"
            : $"{first.DisplayArtist} — {first.DisplayAlbum}";
        AlbumBox.Text = first.Album ?? "";
        ArtistBox.Text = first.AlbumArtist ?? first.Artist ?? "";

        _ = ShowCurrentAsync(first);
        _ = SearchAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _searchCts?.Cancel();
        _downloadCts?.Cancel();
    }

    private async Task ShowCurrentAsync(Track track)
    {
        var current = await CoverCache.GetAsync(track.FilePath, PreviewPixels);
        if (_newCover is not null)
            return; // sudah ada cover baru yang dipilih selama cover lama dimuat
        PreviewImage.Source = current;
        PreviewCaption.Text = current is null ? "No artwork yet" : "Current artwork";
        ShapePanel.Visibility = Visibility.Collapsed;
    }

    // ── Cover Art Archive ────────────────────────────────────

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _results.Clear();

        string album = AlbumBox.Text.Trim();
        string artist = ArtistBox.Text.Trim();
        if (album.Length == 0)
        {
            SearchStatus.Text = "Type an album title to search MusicBrainz.";
            return;
        }

        SearchStatus.Text = "Searching MusicBrainz…";
        SetBusy(SearchRing, true);
        try
        {
            var releases = await App.MusicBrainz.SearchReleasesAsync(album, artist, cts.Token);
            var items = ArtworkCandidate.From(releases).Select(c => new ArtworkItem(c)).ToList();
            foreach (var item in items)
                _results.Add(item);
            SearchStatus.Text = items.Count == 0 ? "" : "Loading covers…";

            // Hasil pencarian tidak menyebut ada tidaknya cover: rilis yang ternyata tidak punya cover disembunyikan
            using var gate = new SemaphoreSlim(ParallelThumbnails);
            await Task.WhenAll(items.Select(async item =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    if (!await item.LoadThumbnailAsync(cts.Token))
                        _results.Remove(item);
                }
                catch (Exception) when (!cts.IsCancellationRequested)
                {
                    _results.Remove(item);
                }
                finally
                {
                    gate.Release();
                }
            }));

            SearchStatus.Text = _results.Count == 0
                ? "No artwork found on MusicBrainz. Try a different album title or artist."
                : $"{Formatters.Count(_results.Count, "cover")} from MusicBrainz (Cover Art Archive). Click one to preview it.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            SearchStatus.Text = $"Couldn't reach MusicBrainz: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                SetBusy(SearchRing, false);
        }
    }

    private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ArtworkItem item)
            return;

        _downloadCts?.Cancel();
        var cts = _downloadCts = new CancellationTokenSource();
        PreviewCaption.Text = "Downloading artwork…";
        PreviewSize.Text = "";
        ShapePanel.Visibility = Visibility.Collapsed;
        SetBusy(PreviewRing, true);
        try
        {
            byte[]? bytes = await App.MusicBrainz.GetFrontCoverAsync(item.Candidate.ReleaseId, EmbedSize, cts.Token)
                ?? await App.MusicBrainz.GetFrontCoverAsync(item.Candidate.ReleaseId, FallbackSize, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            var image = bytes is null ? null : await CoverImageLoader.FromBytesAsync(bytes, PreviewPixels);
            if (bytes is null || image is null)
            {
                PreviewCaption.Text = "Couldn't download this artwork. Choose another one.";
                return;
            }

            string source = item.Detail.Length > 0 ? $"{item.Title} ({item.Detail})" : item.Title;
            SetNewCover(new TagPicture(TagLib.PictureType.FrontCover, "image/jpeg", bytes), image, $"From MusicBrainz: {source}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PreviewCaption.Text = $"Couldn't download the artwork: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                SetBusy(PreviewRing, false);
        }
    }

    // ── File ─────────────────────────────────────────────────

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        // Picker di aplikasi WinUI 3 desktop harus dikaitkan ke jendela pemiliknya
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        byte[] data = await File.ReadAllBytesAsync(file.Path);
        string mime = Path.GetExtension(file.Path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        var image = await CoverImageLoader.FromBytesAsync(data, PreviewPixels);
        if (image is null)
        {
            PreviewCaption.Text = $"\"{file.Name}\" isn't an image this app can read. Choose a JPEG or PNG file.";
            return;
        }

        _downloadCts?.Cancel();
        ResultsGrid.SelectedItem = null;
        SetNewCover(new TagPicture(TagLib.PictureType.FrontCover, mime, data), image, $"From file: {file.Name}");
    }

    private void SetNewCover(TagPicture cover, ImageSource image, string caption)
    {
        _sourceCover = cover;
        _newCover = cover;
        _shapeVersion++;
        PreviewImage.Source = image;
        PreviewCaption.Text = caption;
        ShowSize(cover);

        // Pilihan bentuk hanya untuk cover yang tidak persegi, dan selalu mulai dari "apa adanya"
        var size = ImageInfo.Dimensions(cover.Data);
        ShapePanel.Visibility = size is { } d && !ImageInfo.IsSquare(d.Width, d.Height) ? Visibility.Visible : Visibility.Collapsed;
        _settingShape = true;
        ShapeChoice.SelectedIndex = (int)CoverShape.Original;
        _settingShape = false;
        SaveButton.IsEnabled = !_saving;
    }

    /// <summary>Ukuran piksel (dari header gambarnya) dan ukuran file.</summary>
    private void ShowSize(TagPicture cover)
    {
        string kilobytes = $"{cover.Data.Length / 1024.0:N0} KB";
        PreviewSize.Text = ImageInfo.Dimensions(cover.Data) is { } s ? $"{s.Width} × {s.Height} px · {kilobytes}" : kilobytes;
    }

    /// <summary>Keep original, Crop to square, atau Fit in square: pratinjau dan yang akan ditanam ikut berganti.</summary>
    private async void ShapeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingShape || _sourceCover is not { } source || ShapeChoice.SelectedIndex < 0)
            return;

        var shape = (CoverShape)ShapeChoice.SelectedIndex;
        int version = ++_shapeVersion;
        SaveButton.IsEnabled = false;
        SetBusy(PreviewRing, true);
        try
        {
            var reshaped = await CoverReshaper.ReshapeAsync(source, shape);
            var image = await CoverImageLoader.FromBytesAsync(reshaped.Data, PreviewPixels);
            if (version != _shapeVersion)
                return; // cover atau pilihan lain sudah dipilih selama gambar ini diproses
            if (image is null)
                throw new InvalidOperationException("the result couldn't be read back");

            _newCover = reshaped;
            PreviewImage.Source = image;
            ShowSize(reshaped);
        }
        catch (Exception ex)
        {
            if (version != _shapeVersion)
                return;
            MessageText.Text = $"Couldn't make the artwork square: {ex.Message}. The original will be saved.";
            _newCover = source;
            _settingShape = true;
            ShapeChoice.SelectedIndex = (int)CoverShape.Original;
            _settingShape = false;
        }
        finally
        {
            if (version == _shapeVersion)
            {
                SetBusy(PreviewRing, false);
                SaveButton.IsEnabled = _newCover is not null && !_saving;
            }
        }
    }

    // ── Simpan ───────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _newCover is null || _request is null)
            return;

        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SetBusy(SavingRing, true);
        try
        {
            var paths = _request.Tracks.Select(t => t.FilePath).ToList();
            // Hanya cover depan: informasi lagu dan gambar lain di file tidak disentuh
            var result = await App.TagEdits.SaveAsync(paths, new Dictionary<string, string>(), [_newCover], frontCoverOnly: true);
            if (result.Failed.Count == 0)
            {
                if (Frame.CanGoBack)
                    Frame.GoBack();
                return;
            }

            MessageText.Text = $"Couldn't save {result.Failed.Count} of {paths.Count} songs: "
                + string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})"));
        }
        catch (Exception ex)
        {
            MessageText.Text = $"Couldn't save: {ex.Message}";
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = _newCover is not null;
            CancelButton.IsEnabled = true;
            SetBusy(SavingRing, false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private static void SetBusy(ProgressRing ring, bool busy)
    {
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}

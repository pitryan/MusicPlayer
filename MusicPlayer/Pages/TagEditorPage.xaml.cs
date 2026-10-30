using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MusicPlayer.Pages;

/// <summary>Lagu yang diedit, judul halaman ("Song Info" / "Album Info"), dan apakah dibuka dari halaman album.</summary>
public sealed record TagEditorRequest(IReadOnlyList<string> Paths, string Title, bool FromAlbum = false);

/// <summary>Editor tag untuk satu atau banyak lagu. Hanya field yang diubah yang ditulis ke file.</summary>
public sealed partial class TagEditorPage : Page, IUnsavedChanges
{
    private const int HeaderCoverPixels = 192;
    private const int ArtworkPixels = 520;

    private TagEditorRequest _request = new([], "");
    private List<string> _paths = [];
    private List<TagFieldRow> _rows = [];

    // Perubahan artwork: null = tidak diubah, kosong = dihapus, satu gambar = cover baru
    private IReadOnlyList<TagPicture>? _artworkChange;
    // Ada gambar di salah satu lagu saat dimuat: menghapus artwork dari lagu tanpa gambar bukan perubahan
    private bool _hadArtwork;
    private bool _saving;
    private bool _saved;

    public TagEditorPage()
    {
        InitializeComponent();
    }

    /// <summary>Ada field atau artwork yang diubah dari nilai yang dimuat dan belum disimpan.</summary>
    public bool HasUnsavedChanges => !_saving && !_saved
        && (_rows.Any(r => r.IsChanged) || _artworkChange is { Count: > 0 } || (_artworkChange is not null && _hadArtwork));

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not TagEditorRequest request)
            return;

        _request = request;
        TitleText.Text = request.Title;
        SubtitleText.Text = "Loading…";

        // Tag dibaca di luar UI thread; file yang tidak bisa dibaca dilewati dan disebutkan
        var reads = await Task.Run(() => request.Paths.Select(path =>
        {
            try
            {
                return (Path: path, Values: (TagValues?)TagEditor.Read(path), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Path: path, Values: (TagValues?)null, Error: (string?)ex.Message);
            }
        }).ToList());

        var readable = reads.Where(r => r.Values is not null).ToList();
        _paths = readable.Select(r => r.Path).ToList();
        var values = readable.Select(r => r.Values!).ToList();

        var unreadable = reads.Where(r => r.Values is null).ToList();
        if (unreadable.Count > 0)
            ShowMessage($"Couldn't read {unreadable.Count} of {reads.Count} songs, so they won't be changed: "
                + string.Join("; ", unreadable.Select(r => $"{Path.GetFileName(r.Path)} ({r.Error})")));

        BuildRows(values);
        ShowGroup(TagGroup.Details);
        _ = ShowArtworkAsync(values);

        SubtitleText.Text = values.Count == 1
            ? $"{Display(values[0], "title", Path.GetFileNameWithoutExtension(_paths[0]))} — {Display(values[0], "artist", "Unknown Artist")}"
            : $"{values.Count} songs";
        SaveButton.IsEnabled = values.Count > 0;
        MusicBrainzButton.IsEnabled = values.Count > 0;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        foreach (var row in _rows)
            row.PropertyChanged -= Row_PropertyChanged;
    }

    private static string Display(TagValues values, string key, string fallback) =>
        values.Get(key) is { Length: > 0 } text ? text : fallback;

    /// <summary>Satu baris per field. Nilai yang berbeda antar lagu menjadi "Mixed".</summary>
    private void BuildRows(IReadOnlyList<TagValues> values)
    {
        _rows = TagFields.All.Select(field =>
        {
            var distinct = values.Select(v => v.Get(field.Key)).Distinct(StringComparer.Ordinal).ToList();
            bool mixed = distinct.Count > 1;
            // Field didukung kalau minimal satu lagu bisa menyimpannya; lagu lain dilewati saat menyimpan
            bool supported = values.Count == 0 || values.Any(v => field.IsSupported(v.Format));
            return new TagFieldRow(field, mixed ? "" : distinct.FirstOrDefault() ?? "", mixed, supported,
                "Not stored in M4A files");
        }).ToList();

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e) => MessageText.Visibility = Visibility.Collapsed;

    private void GroupTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        if (selected == ArtworkTab)
        {
            FieldScroller.Visibility = Visibility.Collapsed;
            ArtworkPanel.Visibility = Visibility.Visible;
            return;
        }

        ShowGroup(selected == ReleaseTab ? TagGroup.Release
            : selected == CreditsTab ? TagGroup.Credits
            : selected == SortingTab ? TagGroup.Sorting
            : selected == MusicBrainzTab ? TagGroup.MusicBrainz
            : selected == LyricsTab ? TagGroup.Lyrics
            : TagGroup.Details);
    }

    private void ShowGroup(TagGroup group)
    {
        ArtworkPanel.Visibility = Visibility.Collapsed;
        FieldScroller.Visibility = Visibility.Visible;
        FieldList.ItemsSource = _rows.Where(r => r.Field.Group == group).ToList();
        FieldScroller.ChangeView(null, 0, null, true);
    }

    /// <summary>Cover depan: sama di semua lagu, berbeda ("Mixed"), atau tidak ada.</summary>
    private async Task ShowArtworkAsync(IReadOnlyList<TagValues> values)
    {
        var covers = values
            .Select(v => v.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? v.Pictures.FirstOrDefault())
            .ToList();
        var first = covers.FirstOrDefault();
        bool same = covers.All(c => c is null ? first is null : first is not null && c.Data.AsSpan().SequenceEqual(first.Data));

        _hadArtwork = covers.Any(c => c is not null);
        RemoveArtworkButton.IsEnabled = _hadArtwork;
        if (!same)
        {
            ArtworkStatusText.Text = "Mixed";
        }
        else if (first is null)
        {
            ArtworkStatusText.Text = "No artwork";
        }
        else
        {
            ArtworkStatusText.Text = "";
            ArtworkImage.Source = await CoverImageLoader.FromBytesAsync(first.Data, ArtworkPixels);
        }

        if (first is not null)
            HeaderCover.Source = await CoverImageLoader.FromBytesAsync(first.Data, HeaderCoverPixels);
    }

    private async void ChooseArtwork_Click(object sender, RoutedEventArgs e)
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
        var image = await CoverImageLoader.FromBytesAsync(data, ArtworkPixels);
        if (image is null)
        {
            ShowMessage($"\"{file.Name}\" isn't an image this app can read. Choose a JPEG or PNG file.");
            return;
        }

        _artworkChange = [new TagPicture(TagLib.PictureType.FrontCover, mime, data)];
        ArtworkImage.Source = image;
        ArtworkStatusText.Text = "";
        HeaderCover.Source = await CoverImageLoader.FromBytesAsync(data, HeaderCoverPixels);
        RemoveArtworkButton.IsEnabled = true;
    }

    private void RemoveArtwork_Click(object sender, RoutedEventArgs e)
    {
        _artworkChange = [];
        ArtworkImage.Source = null;
        HeaderCover.Source = null;
        ArtworkStatusText.Text = "No artwork";
        RemoveArtworkButton.IsEnabled = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Leave();

    /// <summary>
    /// Membuka pencarian MusicBrainz untuk lagu-lagu yang sama, dengan judul album dan artis dari tag-nya sebagai awal
    /// pencarian. Perubahan yang belum disimpan di sini tidak ikut terbawa, jadi ditanyakan dulu seperti saat
    /// meninggalkan editor lewat jendela utama.
    /// </summary>
    private async void MusicBrainz_Click(object sender, RoutedEventArgs e)
    {
        if (!await App.MainWindow.ConfirmLeaveAsync())
            return;

        string Hint(string key) => _rows.FirstOrDefault(r => r.Field.Key == key) is { IsMixed: false } row ? row.Text.Trim() : "";
        string artist = Hint("albumartist") is { Length: > 0 } albumArtist ? albumArtist : Hint("artist");
        Frame.Navigate(typeof(MusicBrainzPage), new MusicBrainzRequest(_paths, Hint("album"), artist, _request.FromAlbum), Animations.DrillIn);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving)
            return;

        var changes = _rows.Where(r => r.IsChanged).ToDictionary(r => r.Field.Key, r => r.NewValue);
        if (changes.Count == 0 && _artworkChange is null)
        {
            Leave();
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SavingRing.IsActive = true;
        SavingRing.Visibility = Visibility.Visible;
        try
        {
            // Choose Image… mengganti cover depan saja (gambar lain di file tetap); Remove Artwork menghapus semuanya
            var result = await App.TagEdits.SaveAsync(_paths, changes, _artworkChange, frontCoverOnly: _artworkChange is { Count: > 0 });
            if (result.Failed.Count == 0)
            {
                _saved = true;
                // Pengguna sudah pindah halaman selama menyimpan (misalnya tombol back mouse): jangan ikut mundur dari
                // halaman barunya
                if (ReferenceEquals(Frame.Content, this))
                    LeaveAfterSave(result.Saved);
                return;
            }

            ShowMessage($"Couldn't save {result.Failed.Count} of {_paths.Count} songs: "
                + string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})")));
        }
        catch (Exception ex)
        {
            ShowMessage($"Couldn't save: {ex.Message}");
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            SavingRing.IsActive = false;
            SavingRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Dibuka dari halaman album: kembali ke album itu dengan data baru (nama album bisa saja berubah), dan halaman
    /// album lama serta editor ini dikeluarkan dari riwayat Back. Dari tempat lain: kembali seperti biasa.
    /// </summary>
    private void LeaveAfterSave(IReadOnlyList<Track> saved)
    {
        if (_request.FromAlbum && saved.Count > 0 && App.Database.FindAlbum(saved[0]) is { } album)
        {
            Frame.Navigate(typeof(AlbumDetailPage), album, Animations.DrillIn);
            var history = Frame.BackStack;
            if (history.Count > 0 && history[^1].SourcePageType == typeof(TagEditorPage))
                history.RemoveAt(history.Count - 1);
            if (history.Count > 0 && history[^1].SourcePageType == typeof(AlbumDetailPage))
                history.RemoveAt(history.Count - 1);
            return;
        }

        Leave();
    }

    private void Leave()
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
        else
            Frame.Navigate(typeof(SongsPage));
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }
}

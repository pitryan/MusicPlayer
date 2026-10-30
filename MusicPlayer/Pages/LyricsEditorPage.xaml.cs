using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Helpers;
using MusicPlayer.Library;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Pages;

/// <summary>Editor lirik satu lagu (lihat XAML-nya). Parameter navigasinya lagu (Track).</summary>
public sealed partial class LyricsEditorPage : Page, IUnsavedChanges
{
    private readonly ObservableCollection<LyricEditRow> _rows = [];
    private CancellationTokenSource? _loadCts;
    private Track? _track;
    private bool _synced;
    private bool _saving;
    // Yang akan ditulis Save untuk lirik yang dimuat (atau yang terakhir disimpan); null selama memuat
    private Dictionary<string, string>? _loaded;

    public LyricsEditorPage()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
    }

    /// <summary>
    /// Isi tabel berbeda dari lirik yang dimuat. Dibandingkan dalam bentuk yang akan ditulis Save, jadi baris yang
    /// diketik lalu dikembalikan seperti semula tidak dihitung sebagai perubahan.
    /// </summary>
    public bool HasUnsavedChanges =>
        !_saving && _loaded is { } loaded && BuildChanges().Changes.Any(change => loaded[change.Key] != change.Value);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _track = e.Parameter as Track;
        if (_track is null)
            return;

        TitleText.Text = _track.DisplayTitle;
        SubtitleText.Text = $"{_track.DisplayArtist} — {_track.DisplayAlbum}";
        _ = LoadAsync(_track);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _loadCts?.Cancel();
    }

    /// <summary>
    /// Lirik langsung dari sumbernya (tag, file .lrc, atau simpanan LRCLIB), bukan versi yang sedang tampil di panel
    /// lirik: versi tampilan sudah diberi jeda instrumental tambahan yang tidak boleh ikut tersimpan.
    /// </summary>
    private async Task LoadAsync(Track track)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _loaded = null;
        SetBusy(LoadingRing, true);
        InfoText.Text = "Loading lyrics…";
        try
        {
            var finder = App.Lyrics.Finder;
            var found = await Task.Run(() => finder.FindLocal(track), cts.Token)
                ?? (await finder.FindOnlineAsync(track, cts.Token)).Lyrics;
            if (cts.IsCancellationRequested)
                return;
            ShowRows(found?.Lyrics, keepLayers: false);
            _loaded = BuildChanges().Changes;
            InfoText.Text = found is null
                ? "This song has no lyrics yet. Use Paste Lyrics to add them."
                : $"{LyricsController.DescribeSource(found)} · {Formatters.Count(_rows.Count, "line")}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowRows(null, keepLayers: false);
            _loaded = BuildChanges().Changes;
            InfoText.Text = $"Couldn't load the lyrics ({ex.Message}). You can still paste them.";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                SetBusy(LoadingRing, false);
                SaveButton.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// Mengisi tabel dari lirik. Dengan <paramref name="keepLayers"/>, cara baca dan terjemahan yang sudah ditulis
    /// tetap di urutan barisnya (misalnya saat lirik asli diganti dengan versi bertanda waktu).
    /// </summary>
    private void ShowRows(Lyrics? lyrics, bool keepLayers)
    {
        var readings = keepLayers ? _rows.Select(r => r.Reading).ToList() : [];
        var translations = keepLayers ? _rows.Select(r => r.Translation).ToList() : [];
        _rows.Clear();
        _synced = lyrics?.IsSynced ?? false;
        if (lyrics is not null)
        {
            for (int i = 0; i < lyrics.Lines.Count; i++)
            {
                var line = lyrics.Lines[i];
                _rows.Add(new LyricEditRow(line.Time, lyrics.IsSynced, line.Text,
                    keepLayers ? readings.ElementAtOrDefault(i) : line.Reading,
                    keepLayers ? translations.ElementAtOrDefault(i) : line.Translation,
                    line.Extra));
            }
        }
        PasteReadingButton.IsEnabled = _rows.Count > 0;
        PasteTranslationButton.IsEnabled = _rows.Count > 0;
    }

    // ── Tempel sekaligus ─────────────────────────────────────

    private async void PasteLyrics_Click(object sender, RoutedEventArgs e)
    {
        string? text = await AskForTextAsync("Paste Lyrics",
            "One line per lyric line, or the text of an .lrc file with time tags like [00:12.34].");
        if (text is null)
            return;
        var lyrics = LrcParser.Parse(text);
        if (lyrics is null)
        {
            InfoText.Text = "There were no lyrics in the pasted text.";
            return;
        }
        // LRC tiga lapis yang ditempel membawa cara baca dan terjemahannya sendiri
        ShowRows(lyrics, keepLayers: !LyricLayers.HasLayers(lyrics));
        InfoText.Text = $"{Formatters.Count(lyrics.Lines.Count, "line")}{(lyrics.IsSynced ? ", time-synced" : "")}. Check that the reading and translation still match each line.";
    }

    private async void PasteReading_Click(object sender, RoutedEventArgs e) =>
        await PasteLayerAsync("Paste Reading", "How each line is read (romaji, romanization, pinyin), one line per lyric line.",
            (row, value) => row.Reading = value);

    private async void PasteTranslation_Click(object sender, RoutedEventArgs e) =>
        await PasteLayerAsync("Paste Translation", "The translation, one line per lyric line.",
            (row, value) => row.Translation = value);

    /// <summary>
    /// Teks satu lagu dibagikan ke baris lirik yang berisi, berurutan (LyricLayers.Distribute); kolom itu diganti
    /// seluruhnya. Kalau jumlah barisnya tidak pas, keterangannya disebutkan supaya bisa dicek.
    /// </summary>
    private async Task PasteLayerAsync(string title, string hint, Action<LyricEditRow, string> set)
    {
        string? text = await AskForTextAsync(title, hint);
        if (text is null)
            return;

        var (values, leftover, missing) = LyricLayers.Distribute(_rows.Select(r => r.Original).ToList(), text);
        for (int i = 0; i < _rows.Count; i++)
            set(_rows[i], values[i] ?? "");

        InfoText.Text = (leftover, missing) switch
        {
            (0, 0) => "Every lyric line got a line from the pasted text.",
            ( > 0, _) => $"{Formatters.Count(leftover, "pasted line")} left over. Check that the lines match.",
            _ => $"{Formatters.Count(missing, "lyric line")} got no text. Check that the lines match.",
        };
    }

    private async Task<string?> AskForTextAsync(string title, string hint)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 360,
            Width = 520,
            PlaceholderText = hint,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "Fill",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    // ── Simpan ───────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _track is null)
            return;

        var (rows, changes) = BuildChanges();
        string text = changes["lyrics"];

        // Cara baca dan terjemahan dicocokkan per urutan baris: pastikan liriknya dibaca kembali dengan jumlah baris sama
        if (text.Length > 0 && LrcParser.Parse(text)?.Lines.Count != rows.Count)
        {
            MessageText.Text = "Some lines would change when the lyrics are read back (for example a line that starts with \"[\"), "
                + "so the reading and translation wouldn't line up. Edit those lines and try again.";
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SetBusy(SavingRing, true);
        try
        {
            var result = await App.TagEdits.SaveAsync([_track.FilePath], changes, null);
            if (result.Failed.Count == 0)
            {
                _loaded = changes;
                // Pengguna sudah pindah halaman selama menyimpan (misalnya tombol back mouse): jangan ikut mundur dari
                // halaman barunya
                if (ReferenceEquals(Frame.Content, this) && Frame.CanGoBack)
                    Frame.GoBack();
                return;
            }
            MessageText.Text = $"Couldn't save: {result.Failed[0].Error}";
        }
        catch (Exception ex)
        {
            MessageText.Text = $"Couldn't save: {ex.Message}";
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            SetBusy(SavingRing, false);
        }
    }

    /// <summary>
    /// Yang akan ditulis Save: lirik (LRC), cara baca, dan terjemahan, dari baris yang benar-benar disimpan. Dipakai
    /// juga untuk mengetahui apakah ada perubahan yang belum disimpan.
    /// </summary>
    private (List<LyricEditRow> Rows, Dictionary<string, string> Changes) BuildChanges()
    {
        var rows = _synced ? _rows.ToList() : NormalizePlain(_rows);
        var lyrics = new Lyrics(rows.Select(r => new LyricLine(r.Time, r.Original.Trim()) { Extra = r.Extra }).ToList(), _synced);
        var changes = new Dictionary<string, string>
        {
            ["lyrics"] = rows.Count == 0 ? "" : LrcWriter.Format(lyrics),
            ["lyrics_reading"] = LyricLayers.JoinLines(rows.Select(r => r.Reading)),
            ["lyrics_translation"] = LyricLayers.JoinLines(rows.Select(r => r.Translation)),
        };
        return (rows, changes);
    }

    /// <summary>
    /// Lirik biasa dibaca ulang dengan aturan LrcParser: baris kosong di awal, di akhir, dan yang beruntun dibuang. Baris
    /// yang sama dibuang di sini lebih dulu, bersama cara baca dan terjemahannya, supaya urutan barisnya tetap cocok.
    /// </summary>
    private static List<LyricEditRow> NormalizePlain(IEnumerable<LyricEditRow> rows)
    {
        var kept = new List<LyricEditRow>();
        foreach (var row in rows)
        {
            bool blank = string.IsNullOrWhiteSpace(row.Original);
            if (blank && (kept.Count == 0 || string.IsNullOrWhiteSpace(kept[^1].Original)))
                continue;
            kept.Add(row);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1].Original))
            kept.RemoveAt(kept.Count - 1);
        return kept;
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

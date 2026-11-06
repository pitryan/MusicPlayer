using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Library;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MusicPlayer.Helpers;

/// <summary>
/// Dialog smart playlist seperti di Apple Music: nama, "Match all/any of the following rules", baris-baris aturan (data,
/// operator, nilai), batas jumlah lagu, dan urutan. Jumlah lagu yang cocok tampil langsung selagi aturan diubah.
/// </summary>
public static class SmartPlaylistDialog
{
    /// <summary>Null kalau dibatalkan.</summary>
    public static async Task<(string Name, SmartPlaylistRules Rules)?> ShowAsync(
        string title, string primaryButton, string name, SmartPlaylistRules rules)
    {
        var editor = new SmartPlaylistEditor(name, rules);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = primaryButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = App.MainWindow.Content?.XamlRoot,
        };

        // Baris aturan butuh tempat lebih lebar dari dialog biasa (548 px)
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;
        dialog.IsPrimaryButtonEnabled = editor.IsValid;
        editor.Changed += (_, _) => dialog.IsPrimaryButtonEnabled = editor.IsValid;

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (editor.PlaylistName, editor.Rules) : null;
    }

    /// <summary>
    /// Membuat smart playlist baru, lalu membukanya. Tanpa <paramref name="rules"/>, dimulai dari satu aturan kosong
    /// (Artist contains), seperti di Apple Music.
    /// </summary>
    public static async Task CreateAsync(string name = "", SmartPlaylistRules? rules = null)
    {
        rules ??= new SmartPlaylistRules(MatchAll: true, [new SmartRule(SmartField.Artist, SmartOperator.Contains)]);
        if (await ShowAsync("New Smart Playlist", "Create", name, rules) is { } result)
            App.MainWindow.ShowPlaylist(App.Playlists.CreateSmart(result.Name, result.Rules));
    }

    /// <summary>Mengubah nama dan aturan smart playlist yang sudah ada.</summary>
    public static async Task EditAsync(long id, string name)
    {
        if (App.Playlists.GetRules(id) is not { } rules)
            return;
        if (await ShowAsync("Edit Smart Playlist", "Save", name, rules) is { } result)
            App.Playlists.UpdateSmart(id, result.Name, result.Rules);
    }
}

/// <summary>Isi dialog smart playlist (lihat <see cref="SmartPlaylistDialog"/>).</summary>
internal sealed class SmartPlaylistEditor : StackPanel
{
    private static readonly (SmartField Field, string Label)[] Fields =
    [
        (SmartField.Title, "Title"), (SmartField.Artist, "Artist"), (SmartField.AlbumArtist, "Album Artist"),
        (SmartField.Album, "Album"), (SmartField.Genre, "Genre"), (SmartField.Year, "Year"), (SmartField.Folder, "Folder"),
        (SmartField.Format, "Format"), (SmartField.Quality, "Quality"), (SmartField.SampleRate, "Sample Rate"),
        (SmartField.BitDepth, "Bit Depth"), (SmartField.Duration, "Duration"), (SmartField.DateAdded, "Date Added"),
        (SmartField.PlayCount, "Plays"), (SmartField.LastPlayed, "Last Played"), (SmartField.Favourite, "Favourite"),
        (SmartField.HasLyrics, "Has Lyrics"),
    ];

    private static readonly (SmartOrder Order, string Label)[] Orders =
    [
        (SmartOrder.Album, "Album"), (SmartOrder.Title, "Title"), (SmartOrder.Artist, "Artist"),
        (SmartOrder.RecentlyAdded, "Recently Added"), (SmartOrder.MostPlayed, "Most Played"),
        (SmartOrder.RecentlyPlayed, "Recently Played"), (SmartOrder.Random, "Random"),
    ];

    private static readonly (QualityKind Kind, string Label)[] Qualities =
        [(QualityKind.HiRes, "Hi-Res"), (QualityKind.Lossless, "Lossless"), (QualityKind.Lossy, "Lossy")];

    private readonly TextBox _name;
    private readonly ComboBox _match;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly CheckBox _limitCheck;
    private readonly NumberBox _limit;
    private readonly ComboBox _order;
    private readonly TextBlock _preview;
    private readonly DispatcherQueueTimer _previewTimer;
    private readonly List<RuleRow> _ruleRows = [];

    public SmartPlaylistEditor(string name, SmartPlaylistRules rules)
    {
        Spacing = 16;
        MinWidth = 720;

        _name = new TextBox { Header = "Name", Text = name, PlaceholderText = "Playlist name", MaxLength = 200 };
        _name.TextChanged += (_, _) => OnChanged(refreshPreview: false);
        _name.Loaded += (_, _) =>
        {
            _name.Focus(FocusState.Programmatic);
            _name.SelectAll();
        };
        Children.Add(_name);

        // "Match [all] of the following rules:"
        _match = new ComboBox { Items = { "all", "any" }, SelectedIndex = rules.MatchAll ? 0 : 1, MinWidth = 80 };
        _match.SelectionChanged += (_, _) => OnChanged();
        Children.Add(Line(Label("Match"), _match, Label("of the following rules:")));

        foreach (var rule in rules.Rules)
            AddRow(rule);
        Children.Add(_rows);

        var add = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE710", FontSize = 12 }, new TextBlock { Text = "Add Rule" } } } };
        add.Click += (_, _) =>
        {
            AddRow(new SmartRule(SmartField.Artist, SmartOperator.Contains));
            OnChanged();
        };
        Children.Add(add);

        // "[x] Limit to [25] songs, sorted by [Most Played]": dengan batas, lagu yang diambil menurut urutan ini
        _limitCheck = new CheckBox { Content = "Limit to", IsChecked = rules.Limit is not null, MinWidth = 0 };
        _limit = new NumberBox { Value = rules.Limit ?? 25, Minimum = 1, Maximum = 100000, Width = 100, IsEnabled = rules.Limit is not null };
        _limitCheck.Checked += (_, _) => { _limit.IsEnabled = true; OnChanged(); };
        _limitCheck.Unchecked += (_, _) => { _limit.IsEnabled = false; OnChanged(); };
        _limit.ValueChanged += (_, _) => OnChanged();
        _order = new ComboBox { MinWidth = 160 };
        foreach (var (_, label) in Orders)
            _order.Items.Add(label);
        _order.SelectedIndex = Array.FindIndex(Orders, o => o.Order == rules.Order);
        _order.SelectionChanged += (_, _) => OnChanged();
        Children.Add(Line(_limitCheck, _limit, Label("songs"), Label("·  Sorted by"), _order));

        _preview = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SecondaryTextBrush"] };
        Children.Add(_preview);

        // Jumlah lagu dihitung ulang sebentar setelah aturan berhenti diubah, bukan di setiap ketukan
        _previewTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(250);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    /// <summary>Dipicu setiap kali isinya berubah, untuk mengaktifkan tombol simpan.</summary>
    public event EventHandler? Changed;

    public string PlaylistName => _name.Text.Trim();

    public SmartPlaylistRules Rules => new(
        _match.SelectedIndex != 1,
        _ruleRows.Select(r => r.Rule).ToList(),
        Orders[Math.Max(0, _order.SelectedIndex)].Order,
        _limitCheck.IsChecked == true && !double.IsNaN(_limit.Value) && _limit.Value >= 1 ? (int)_limit.Value : null);

    /// <summary>Nama terisi, setiap aturan lengkap, dan batas jumlah lagu (kalau dipakai) berupa angka.</summary>
    public bool IsValid =>
        PlaylistName.Length > 0
        && _ruleRows.All(r => SmartPlaylists.IsComplete(r.Rule))
        && (_limitCheck.IsChecked != true || (!double.IsNaN(_limit.Value) && _limit.Value >= 1));

    private void OnChanged(bool refreshPreview = true)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        if (refreshPreview)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private void UpdatePreview()
    {
        if (!_ruleRows.All(r => SmartPlaylists.IsComplete(r.Rule)))
        {
            _preview.Text = "Complete every rule to see which songs match.";
            return;
        }

        var tracks = App.Playlists.Preview(Rules);
        var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        _preview.Text = tracks.Count == 0
            ? "No songs match these rules yet."
            : $"{Formatters.Count(tracks.Count, "song")} match · {Formatters.LongDuration(total)}";
    }

    private void AddRow(SmartRule rule)
    {
        var row = new RuleRow(rule, OnRowChanged, RemoveRow);
        _ruleRows.Add(row);
        _rows.Children.Add(row.Root);
    }

    private void OnRowChanged() => OnChanged();

    private void RemoveRow(RuleRow row)
    {
        _ruleRows.Remove(row);
        _rows.Children.Remove(row.Root);
        OnChanged();
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Line(params FrameworkElement[] items)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items)
        {
            item.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(item);
        }
        return line;
    }

    /// <summary>Satu baris aturan: data, operator, isian nilai yang menyesuaikan jenis datanya, dan tombol hapus.</summary>
    private sealed class RuleRow
    {
        private readonly Action _changed;
        private readonly ComboBox _field = new() { Width = 150 };
        private readonly ComboBox _operator = new() { Width = 170 };
        private readonly Grid _valueArea = new();
        private Func<string> _value = () => "";

        public RuleRow(SmartRule rule, Action changed, Action<RuleRow> remove)
        {
            _changed = changed;
            foreach (var (_, label) in Fields)
                _field.Items.Add(label);

            var removeButton = new Button { Content = new FontIcon { Glyph = "\uE738", FontSize = 12 }, Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(removeButton, "Remove rule");
            removeButton.Click += (_, _) => remove(this);

            Root = new Grid { ColumnSpacing = 8 };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_operator, 1);
            Grid.SetColumn(_valueArea, 2);
            Grid.SetColumn(removeButton, 3);
            Root.Children.Add(_field);
            Root.Children.Add(_operator);
            Root.Children.Add(_valueArea);
            Root.Children.Add(removeButton);

            Show(rule);
            _field.SelectionChanged += (_, _) =>
            {
                // Data lain: operator dan isiannya ikut berganti, nilainya dimulai dari kosong
                var field = Fields[_field.SelectedIndex].Field;
                Show(new SmartRule(field, SmartPlaylists.OperatorsFor(field)[0]));
                _changed();
            };
            _operator.SelectionChanged += (_, _) => _changed();
        }

        public Grid Root { get; }

        private SmartField Field => Fields[Math.Max(0, _field.SelectedIndex)].Field;

        public SmartRule Rule
        {
            get
            {
                var operators = SmartPlaylists.OperatorsFor(Field);
                return new SmartRule(Field, operators[Math.Clamp(_operator.SelectedIndex, 0, operators.Count - 1)], _value());
            }
        }

        private void Show(SmartRule rule)
        {
            _field.SelectedIndex = Array.FindIndex(Fields, f => f.Field == rule.Field);

            _operator.Items.Clear();
            var operators = SmartPlaylists.OperatorsFor(rule.Field);
            foreach (var op in operators)
                _operator.Items.Add(OperatorLabel(rule.Field, op));
            _operator.SelectedIndex = Math.Max(0, operators.ToList().IndexOf(rule.Operator));

            _valueArea.Children.Clear();
            _valueArea.ColumnDefinitions.Clear();
            switch (SmartPlaylists.KindOf(rule.Field))
            {
                case SmartFieldKind.Text:
                {
                    var box = new TextBox { Text = rule.Value };
                    box.TextChanged += (_, _) => _changed();
                    _value = () => box.Text;
                    _valueArea.Children.Add(box);
                    break;
                }
                case SmartFieldKind.Number or SmartFieldKind.Date:
                {
                    // TextBox, bukan NumberBox: NumberBox baru melaporkan nilainya setelah fokus pindah, jadi tombol simpan
                    // akan tetap mati selama angkanya diketik. Koma dan titik desimal sama-sama diterima (lihat SmartPlaylists).
                    var box = new TextBox { Text = rule.Value, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
                    box.InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } };
                    box.TextChanged += (_, _) => _changed();
                    _value = () => box.Text.Trim();
                    AddWithUnit(box, Unit(rule.Field));
                    break;
                }
                case SmartFieldKind.Quality:
                {
                    var choice = new ComboBox { MinWidth = 130 };
                    foreach (var (_, label) in Qualities)
                        choice.Items.Add(label);
                    int index = Array.FindIndex(SmartPlaylists.QualityNames, q => string.Equals(q.Name, rule.Value, StringComparison.OrdinalIgnoreCase));
                    choice.SelectedIndex = index >= 0 ? index : 0;
                    choice.SelectionChanged += (_, _) => _changed();
                    _value = () => SmartPlaylists.QualityNames[Math.Max(0, choice.SelectedIndex)].Name;
                    _valueArea.Children.Add(choice);
                    break;
                }
                case SmartFieldKind.Folder:
                {
                    var box = new TextBox { Text = rule.Value, PlaceholderText = @"D:\Music\Japan" };
                    box.TextChanged += (_, _) => _changed();
                    var browse = new Button { Content = "Browse…" };
                    browse.Click += async (_, _) =>
                    {
                        var picker = new FolderPicker();
                        picker.FileTypeFilter.Add("*");
                        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                        if (await picker.PickSingleFolderAsync() is { } folder)
                            box.Text = folder.Path;
                    };
                    _value = () => box.Text.Trim();
                    AddWithUnit(box, browse);
                    break;
                }
                default:
                    _value = () => ""; // Favourite, Has Lyrics: cukup "yes" atau "no" di operatornya
                    break;
            }
        }

        private void AddWithUnit(FrameworkElement value, object? unit)
        {
            _valueArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _valueArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _valueArea.ColumnSpacing = 8;
            _valueArea.Children.Add(value);
            if (unit is null)
                return;

            var element = unit as FrameworkElement ?? new TextBlock { Text = (string)unit, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(element, 1);
            _valueArea.Children.Add(element);
        }

        private static string? Unit(SmartField field) => field switch
        {
            SmartField.SampleRate => "kHz",
            SmartField.BitDepth => "bit",
            SmartField.Duration => "minutes",
            SmartField.DateAdded or SmartField.LastPlayed => "days",
            SmartField.PlayCount => "times",
            _ => null,
        };

        private static string OperatorLabel(SmartField field, SmartOperator op) => (SmartPlaylists.KindOf(field), op) switch
        {
            (SmartFieldKind.Boolean, SmartOperator.Is) => "yes",
            (SmartFieldKind.Boolean, _) => "no",
            (SmartFieldKind.Folder, SmartOperator.Is) => "is in",
            (SmartFieldKind.Folder, _) => "is not in",
            (_, SmartOperator.GreaterThan) when field == SmartField.Duration => "is longer than",
            (_, SmartOperator.LessThan) when field == SmartField.Duration => "is shorter than",
            (_, SmartOperator.GreaterThan) when field == SmartField.Year => "is after",
            (_, SmartOperator.LessThan) when field == SmartField.Year => "is before",
            (_, SmartOperator.Contains) => "contains",
            (_, SmartOperator.DoesNotContain) => "does not contain",
            (_, SmartOperator.Is) => "is",
            (_, SmartOperator.IsNot) => "is not",
            (_, SmartOperator.StartsWith) => "starts with",
            (_, SmartOperator.EndsWith) => "ends with",
            (_, SmartOperator.GreaterThan) => "is more than",
            (_, SmartOperator.LessThan) => "is less than",
            (_, SmartOperator.InTheLast) => "is in the last",
            _ => "is not in the last",
        };
    }
}

using Microsoft.UI.Xaml;
using MusicPlayer.Helpers;
using MusicPlayer.Library;

namespace MusicPlayer.ViewModels;

/// <summary>
/// Satu baris di editor tag. Kalau beberapa lagu diedit sekaligus dan isinya berbeda, barisnya "Mixed": dibiarkan
/// kosong berarti setiap lagu tetap dengan nilainya sendiri, diisi berarti semua lagu mendapat nilai itu.
/// </summary>
public sealed class TagFieldRow : ObservableObject
{
    private string _text;
    private bool? _isChecked;

    public TagFieldRow(TagField field, string value, bool isMixed, bool isSupported, string unsupportedReason)
    {
        Field = field;
        IsMixed = isMixed;
        IsSupported = isSupported;
        OriginalText = isMixed ? "" : value;
        _text = OriginalText;
        OriginalChecked = isMixed ? null : value == "1";
        _isChecked = OriginalChecked;
        Placeholder = !isSupported ? unsupportedReason
            : isMixed ? "Mixed"
            : field.MultiValue ? "Separate multiple values with ;"
            : "";
    }

    public TagField Field { get; }

    public string Label => Field.Label;

    public bool IsMixed { get; }

    public bool IsSupported { get; }

    public string Placeholder { get; }

    public string OriginalText { get; }

    public bool? OriginalChecked { get; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>Compilation ditampilkan sebagai kotak centang (di file disimpan sebagai "1").</summary>
    public bool IsCheckBox => Field.Key == "compilation";

    public bool IsLongText => Field.Key is "lyrics" or "comment";

    public double TextMinHeight => Field.Key == "lyrics" ? 420 : IsLongText ? 72 : 0;

    public Visibility TextVisibility => IsCheckBox ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CheckVisibility => IsCheckBox ? Visibility.Visible : Visibility.Collapsed;

    public bool IsChanged => IsCheckBox
        ? IsChecked != OriginalChecked && IsChecked is not null
        : IsMixed ? Text.Trim().Length > 0 : Text.Trim() != OriginalText.Trim();

    /// <summary>Nilai yang ditulis ke file; kosong berarti field-nya dihapus.</summary>
    public string NewValue => IsCheckBox ? (IsChecked == true ? "1" : "") : Text;
}

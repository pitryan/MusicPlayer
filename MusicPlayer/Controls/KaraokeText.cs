using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Library;

namespace MusicPlayer.Controls;

/// <summary>
/// Teks satu baris lirik yang bisa diisi seperti karaoke Apple Music: huruf yang belum dinyanyikan redup, lalu menyala
/// satu per satu dari kiri. Setiap huruf menjadi satu Run dengan kuasnya sendiri, jadi tata letak dan pemenggalan
/// barisnya tetap diatur TextBlock. Waktu per huruf direncanakan oleh <see cref="KaraokeTiming"/>.
/// </summary>
internal sealed class KaraokeText
{
    /// <summary>
    /// Kecerahan huruf yang belum dinyanyikan, relatif terhadap huruf yang sudah. Tidak lebih redup dari baris
    /// berikutnya di layar penuh (0,55), supaya baris yang sedang dinyanyikan tetap yang paling terang.
    /// </summary>
    public const double UnsungOpacity = 0.55;

    private readonly KaraokeUnit[] _units;
    private readonly Run[] _runs;
    private readonly SolidColorBrush?[] _brushes;
    private readonly byte[] _alphas;
    private readonly double _totalWeight;

    public KaraokeText(TextBlock block, IReadOnlyList<KaraokeUnit> units)
    {
        _units = [.. units];
        _runs = new Run[_units.Length];
        _brushes = new SolidColorBrush?[_units.Length];
        _alphas = new byte[_units.Length];
        for (int i = 0; i < _units.Length; i++)
        {
            _runs[i] = new Run { Text = _units[i].Text };
            block.Inlines.Add(_runs[i]);
        }
        _totalWeight = _units.Sum(u => u.Weight);
        Duration = _units.Length == 0 ? 0 : _units.Max(u => u.Start + u.Length);
    }

    /// <summary>Kapan huruf terakhir selesai menyala, dalam satuan waktu rencananya.</summary>
    public double Duration { get; }

    /// <summary>
    /// Mengisi huruf sampai waktu <paramref name="time"/> (satuan yang sama dengan rencananya). Hasilnya bagian baris
    /// yang sudah menyala (0–1, menurut bobot huruf), supaya baris lain (cara baca) bisa diisi sejauh itu.
    /// </summary>
    public double Fill(double time)
    {
        double lit = 0;
        for (int i = 0; i < _units.Length; i++)
        {
            var unit = _units[i];
            double amount = unit.Length > 0 ? Math.Clamp((time - unit.Start) / unit.Length, 0, 1) : time >= unit.Start ? 1 : 0;
            lit += amount * unit.Weight;

            // Spasi tidak terlihat, jadi tidak perlu kuas
            if (unit.IsSpace)
                continue;

            byte alpha = (byte)Math.Round(255 * (UnsungOpacity + (1 - UnsungOpacity) * amount));
            if (_brushes[i] is not null && _alphas[i] == alpha)
                continue;

            _alphas[i] = alpha;
            if (_brushes[i] is null)
            {
                _brushes[i] = new SolidColorBrush();
                _runs[i].Foreground = _brushes[i];
            }
            _brushes[i]!.Color = ColorHelper.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
        }
        return _totalWeight > 0 ? lit / _totalWeight : time >= Duration ? 1 : 0;
    }

    /// <summary>Kembali ke teks biasa (warna dari TextBlock), untuk baris yang tidak sedang dinyanyikan.</summary>
    public void Reset()
    {
        for (int i = 0; i < _runs.Length; i++)
        {
            if (_brushes[i] is null)
                continue;
            _runs[i].ClearValue(TextElement.ForegroundProperty);
            _brushes[i] = null;
        }
    }
}

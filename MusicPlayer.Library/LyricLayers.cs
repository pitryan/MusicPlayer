namespace MusicPlayer.Library;

/// <summary>
/// Cara baca dan terjemahan lirik (tag LYRICS_READING dan LYRICS_TRANSLATION): teks biasa, satu baris per baris
/// lirik asli, urut seperti baris hasil LrcParser. Baris kosong berarti baris itu tidak punya cara baca atau
/// terjemahan.
/// </summary>
public static class LyricLayers
{
    public static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Teks untuk disimpan: satu baris per baris lirik. Baris baru di dalam satu nilai diganti spasi (supaya urutan
    /// baris tidak bergeser), dan baris kosong di akhir dibuang; kalau semuanya kosong, hasilnya "" (tag dihapus).
    /// </summary>
    public static string JoinLines(IEnumerable<string?> values)
    {
        var lines = values.Select(v => (v ?? "").Replace("\r", "").Replace('\n', ' ').Trim()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    /// <summary>Menempelkan cara baca dan terjemahan ke baris lirik yang sama urutannya.</summary>
    public static Lyrics Attach(Lyrics lyrics, string? reading, string? translation)
    {
        var readings = SplitLines(reading);
        var translations = SplitLines(translation);
        if (readings.Count == 0 && translations.Count == 0)
            return lyrics;

        var lines = lyrics.Lines
            .Select((line, i) => line with { Reading = At(readings, i), Translation = At(translations, i) })
            .ToList();
        return new Lyrics(lines, lyrics.IsSynced);
    }

    /// <summary>
    /// Teks yang ditempel sekaligus (misalnya terjemahan satu lagu dari sebuah situs) dibagikan berurutan ke baris
    /// lirik yang berisi. Baris kosong di kedua sisi dilewati, karena teks dari situs jarang punya baris untuk jeda
    /// instrumental. Leftover: baris tempelan yang tidak kebagian; Missing: baris lirik yang tidak mendapat teks.
    /// </summary>
    public static (string?[] Values, int Leftover, int Missing) Distribute(IReadOnlyList<string> originals, string pasted)
    {
        var pastedLines = SplitLines(pasted).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var values = new string?[originals.Count];
        int next = 0, missing = 0;
        for (int i = 0; i < originals.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(originals[i]))
                continue;
            if (next < pastedLines.Count)
                values[i] = pastedLines[next++];
            else
                missing++;
        }
        return (values, pastedLines.Count - next, missing);
    }

    private static string? At(IReadOnlyList<string> lines, int index) =>
        index < lines.Count && lines[index].Trim() is { Length: > 0 } text ? text : null;
}

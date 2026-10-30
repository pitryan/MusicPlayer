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

    /// <summary>
    /// Menempelkan cara baca dan terjemahan ke baris lirik yang sama urutannya. Lapisan yang sudah ada di liriknya
    /// (dari LRC tiga lapis) tetap dipakai untuk baris yang tidak punya isian di tag.
    /// </summary>
    public static Lyrics Attach(Lyrics lyrics, string? reading, string? translation)
    {
        var readings = SplitLines(reading);
        var translations = SplitLines(translation);
        if (readings.Count == 0 && translations.Count == 0)
            return lyrics;

        var lines = lyrics.Lines
            .Select((line, i) => line with
            {
                Reading = At(readings, i) ?? line.Reading,
                Translation = At(translations, i) ?? line.Translation,
            })
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

    /// <summary>True kalau ada baris yang punya cara baca atau terjemahan.</summary>
    public static bool HasLayers(Lyrics? lyrics) =>
        lyrics?.Lines.Any(l => l.Reading is not null || l.Translation is not null) == true;

    // Paling banyak berapa baris sumber yang boleh digabung menjadi satu baris tujuan
    private const int MaxMerge = 3;

    /// <summary>
    /// Memindahkan cara baca dan terjemahan dari <paramref name="from"/> ke <paramref name="to"/>, dengan mencocokkan
    /// teks barisnya. Dipakai saat lirik yang tampil (misalnya bertanda waktu dari LRCLIB atau file .lrc) berbeda dari
    /// lirik di tag yang menyimpan lapisannya: urutan, jumlah, dan penulisan barisnya bisa berbeda, jadi tidak bisa
    /// dipasangkan menurut nomor baris seperti <see cref="Attach"/>.
    /// Yang dianggap sama: teks yang sama setelah spasi, tanda baca, dan bagian dalam kurung dibuang (LRCLIB sering
    /// menulis gema penyanyi latar, misalnya "もうちょっとなのに（なのに）"), atau satu baris tujuan yang merupakan gabungan
    /// 2–3 baris sumber berurutan (lapisannya digabung dengan spasi). Pencocokan dilakukan berurutan seperti diff,
    /// supaya reff yang berulang tetap berpasangan dengan pengulangan yang benar. Baris tanpa pasangan tidak mendapat
    /// lapisan; lirik tujuan tidak diubah selain itu.
    /// </summary>
    public static Lyrics Transfer(Lyrics from, Lyrics to)
    {
        if (!HasLayers(from))
            return to;

        var source = from.Lines.Where(l => !l.IsBlank).ToList();
        var sourceKeys = source.Select(l => Key(l.Text)).ToList();
        var targetIndex = Enumerable.Range(0, to.Lines.Count).Where(i => !to.Lines[i].IsBlank).ToList();
        var targetKeys = targetIndex.Select(i => Key(to.Lines[i].Text)).ToList();
        int n = source.Count, m = targetIndex.Count;

        // Skor terbaik untuk sisa baris sumber mulai i dan sisa baris tujuan mulai j (DP dari belakang).
        // Setiap baris sumber yang terpasang bernilai 2, jadi gabungan 2 baris lebih disukai daripada 1 cocok + 1 lewat.
        var score = new int[n + 1, m + 1];
        var step = new int[n + 1, m + 1]; // 0 = lewati sumber, -1 = lewati tujuan, k > 0 = k baris sumber → 1 tujuan
        for (int i = n; i >= 0; i--)
        {
            for (int j = m; j >= 0; j--)
            {
                if (i == n || j == m)
                    continue; // skor 0, tidak ada pasangan lagi

                int best = score[i + 1, j], choice = 0;
                if (score[i, j + 1] > best)
                    (best, choice) = (score[i, j + 1], -1);
                for (int k = 1; k <= MaxMerge && i + k <= n; k++)
                {
                    if (!Matches(sourceKeys, i, k, targetKeys[j]))
                        continue;
                    int value = 2 * k + score[i + k, j + 1];
                    if (value > best)
                        (best, choice) = (value, k);
                }
                score[i, j] = best;
                step[i, j] = choice;
            }
        }

        var lines = to.Lines.ToArray();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            int k = step[i, j];
            if (k == 0) { i++; continue; }
            if (k < 0) { j++; continue; }

            var group = source.GetRange(i, k);
            int index = targetIndex[j];
            lines[index] = lines[index] with
            {
                // Lapisan yang sudah dimiliki baris tujuan (LRC tiga lapis) tidak dihapus kalau sumbernya kosong
                Reading = JoinLayer(group.Select(l => l.Reading)) ?? lines[index].Reading,
                Translation = JoinLayer(group.Select(l => l.Translation)) ?? lines[index].Translation,
            };
            i += k;
            j++;
        }
        return new Lyrics(lines, to.IsSynced);
    }

    private static bool Matches(List<string> sourceKeys, int start, int count, string target)
    {
        if (target.Length == 0)
            return false;
        string joined = string.Concat(sourceKeys.GetRange(start, count));
        if (joined.Length == 0)
            return false;
        if (joined == target)
            return true;

        // Satu baris boleh sedikit berbeda (misalnya tambahan kata di salah satu versi), asal salah satunya memuat
        // yang lain dan panjangnya hampir sama. Untuk gabungan beberapa baris harus persis, supaya tidak asal menempel.
        if (count > 1)
            return false;
        var (shorter, longer) = joined.Length <= target.Length ? (joined, target) : (target, joined);
        return longer.Contains(shorter, StringComparison.Ordinal) && shorter.Length * 10 >= longer.Length * 7;
    }

    private static string? JoinLayer(IEnumerable<string?> values)
    {
        var parts = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static readonly System.Text.RegularExpressions.Regex Bracketed =
        new(@"[（(\[［][^）)\]］]*[）)\]］]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Kunci pembanding baris: huruf lebar penuh disamakan (NFKC), huruf kecil, tanpa bagian dalam kurung, hanya huruf
    /// dan angka. Kalau baris itu seluruhnya di dalam kurung, isi kurungnya yang dipakai.
    /// </summary>
    private static string Key(string text)
    {
        string normalized = text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        string key = LettersOnly(Bracketed.Replace(normalized, ""));
        return key.Length > 0 ? key : LettersOnly(normalized);
    }

    private static string LettersOnly(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit));
}

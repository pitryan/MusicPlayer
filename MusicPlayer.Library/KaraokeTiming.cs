using System.Globalization;
using System.Text;

namespace MusicPlayer.Library;

/// <summary>Satu huruf yang diisi karaoke: kapan mulai menyala dan berapa lama, dalam detik sejak baris mulai.</summary>
public readonly record struct KaraokeUnit(string Text, double Start, double Length, double Weight)
{
    public bool IsSpace => Text.Length > 0 && char.IsWhiteSpace(Text[0]);
}

/// <summary>
/// Kapan setiap huruf sebuah baris dinyanyikan, untuk animasi karaoke. Kalau barisnya punya waktu per kata (Enhanced
/// LRC), huruf menyala tepat di waktu katanya. Kalau tidak (LRC biasa dan LRCLIB hanya punya waktu per baris), baris
/// dianggap dinyanyikan merata: setiap huruf diberi bobot kira-kira sebanyak suku kata (kana 1, kanji 1,5, huruf Latin
/// sepertiga), lalu baris diisi dengan kecepatan nyanyian yang wajar, tidak melewati baris berikutnya.
/// </summary>
public static class KaraokeTiming
{
    // Kecepatan nyanyian yang dianggap wajar, dalam suku kata per detik
    private const double SyllablesPerSecond = 6;

    // Kalau jarak ke baris berikutnya panjang (misalnya ada jeda), pengisian paling lambat sekian kali kecepatan wajar
    private const double SlowestFactor = 1.8;

    // Pengisian selesai sedikit sebelum baris berikutnya mulai
    private const double GapShare = 0.92;

    /// <summary>
    /// Rencana pengisian satu baris. <paramref name="untilNextLine"/>: waktu sampai baris berikutnya menyala.
    /// <paramref name="fillWholeGap"/>: diisi merata selama seluruh jarak itu (titik jeda instrumental).
    /// </summary>
    public static KaraokeUnit[] Plan(string text, IReadOnlyList<LyricWord> words, TimeSpan? wordsEnd, TimeSpan untilNextLine,
        bool fillWholeGap = false)
    {
        var (units, weights) = Split(text);
        if (words.Count > 0 && FromWords(units, weights, words, wordsEnd, untilNextLine.TotalSeconds) is { } timed)
            return timed;

        double seconds = fillWholeGap
            ? untilNextLine.TotalSeconds
            : FillDuration(weights.Sum(), untilNextLine).TotalSeconds;
        return Spread(units, weights, 0, seconds);
    }

    /// <summary>Huruf-huruf teks disebar merata (menurut bobotnya) dari 0 sampai <paramref name="length"/>.</summary>
    public static KaraokeUnit[] Spread(string text, double length)
    {
        var (units, weights) = Split(text);
        return Spread(units, weights, 0, length);
    }

    /// <summary>Huruf-huruf baris (text element, jadi huruf gabungan tetap utuh) beserta bobotnya.</summary>
    public static (string[] Units, double[] Weights) Split(string text)
    {
        var units = new List<string>();
        var weights = new List<double>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string unit = elements.GetTextElement();
            units.Add(unit);
            weights.Add(Weight(Rune.GetRuneAt(unit, 0)));
        }
        return ([.. units], [.. weights]);
    }

    /// <summary>Lama baris diisi: secepat nyanyian wajar, dipadatkan kalau baris berikutnya datang lebih cepat.</summary>
    public static TimeSpan FillDuration(double totalWeight, TimeSpan untilNextLine)
    {
        double natural = totalWeight / SyllablesPerSecond;
        double seconds = Math.Min(untilNextLine.TotalSeconds * GapShare, natural * SlowestFactor);
        return TimeSpan.FromSeconds(Math.Max(seconds, 0.1));
    }

    private static KaraokeUnit[] Spread(string[] units, double[] weights, double start, double length)
    {
        double total = weights.Sum();
        var result = new KaraokeUnit[units.Length];
        double at = start;
        for (int i = 0; i < units.Length; i++)
        {
            double share = total > 0 ? length * weights[i] / total : 0;
            result[i] = new KaraokeUnit(units[i], at, share, weights[i]);
            at += share;
        }
        return result;
    }

    /// <summary>
    /// Huruf diberi waktu dari kata yang memuatnya: setiap kata menyala dari waktunya sampai kata berikutnya (kata
    /// terakhir sampai WordsEnd, atau secepat nyanyian wajar), dan huruf di dalamnya disebar menurut bobotnya. Kata
    /// dicocokkan dengan huruf baris tanpa memedulikan spasi; null kalau tidak cocok (baris tetap diisi perkiraan).
    /// </summary>
    private static KaraokeUnit[]? FromWords(string[] units, double[] weights, IReadOnlyList<LyricWord> words,
        TimeSpan? wordsEnd, double untilNext)
    {
        var counts = words.Select(w => Split(w.Text).Units.Count(u => !char.IsWhiteSpace(u[0]))).ToArray();
        if (counts.Sum() != units.Count(u => !char.IsWhiteSpace(u[0])))
            return null;

        var result = new KaraokeUnit[units.Length];
        int next = 0;
        double end = 0;
        for (int k = 0; k < words.Count; k++)
        {
            // Spasi sebelum kata menyala bersama kata itu
            var letters = new List<int>();
            while (letters.Count < counts[k])
            {
                if (char.IsWhiteSpace(units[next][0]))
                    result[next] = new KaraokeUnit(units[next], words[k].Start.TotalSeconds, 0, 0);
                else
                    letters.Add(next);
                next++;
            }

            double start = words[k].Start.TotalSeconds;
            double weight = letters.Sum(i => weights[i]);
            end = k + 1 < words.Count
                ? words[k + 1].Start.TotalSeconds
                : wordsEnd?.TotalSeconds ?? Math.Min(start + weight / SyllablesPerSecond * SlowestFactor, untilNext);
            double length = Math.Max(end - start, 0.05);
            double at = start;
            foreach (int i in letters)
            {
                double share = weight > 0 ? length * weights[i] / weight : 0;
                result[i] = new KaraokeUnit(units[i], at, share, weights[i]);
                at += share;
            }
        }
        for (; next < units.Length; next++)
            result[next] = new KaraokeUnit(units[next], end, 0, 0);
        return result;
    }

    // Dihitung dari code point (Rune), supaya huruf di luar BMP seperti kanji CJK Ext-B tidak dianggap tanda baca
    private static double Weight(Rune rune)
    {
        int c = rune.Value;
        if (Rune.IsWhiteSpace(rune))
            return 0;
        if (c == '•')
            return 1; // titik jeda instrumental, diisi satu per satu
        if (IsSmallKana(c))
            return 0.3; // ゃ, ゅ, ょ, dan sejenisnya menyatu dengan huruf sebelumnya
        if (c is >= 0x3040 and <= 0x30FF or >= 0xAC00 and <= 0xD7AF)
            return 1; // hiragana, katakana (termasuk ー), dan suku kata Hangul
        if (c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x3FFFF)
            return 1.5; // kanji biasanya dibaca lebih dari satu suku kata
        if (Rune.IsLetterOrDigit(rune))
            return 1.0 / 3; // huruf Latin: kira-kira tiga huruf per suku kata
        return 0.2; // tanda baca dan simbol
    }

    private static bool IsSmallKana(int c) => c < 0x10000 && "ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ".Contains((char)c);
}

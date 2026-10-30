using System.Text;

namespace MusicPlayer.Library;

/// <summary>
/// Menulis lirik kembali sebagai teks untuk tag LYRICS: LRC bertanda waktu ([mm:ss.xx]) kalau liriknya bertanda waktu,
/// teks biasa kalau tidak. Baris tambahan dengan waktu yang sama (Extra) ikut ditulis, jadi LRC dwibahasa tetap utuh,
/// begitu juga waktu per kata (Enhanced LRC). Hasilnya dibaca kembali LrcParser menjadi baris yang sama.
/// </summary>
public static class LrcWriter
{
    public static string Format(Lyrics lyrics)
    {
        var text = new StringBuilder();
        foreach (var line in lyrics.Lines)
        {
            string prefix = lyrics.IsSynced ? Stamp(line.Time) : "";
            text.Append(prefix).Append(lyrics.IsSynced ? WithWordTags(line) : line.Text).Append('\n');
            foreach (string extra in line.Extra)
                text.Append(prefix).Append(extra).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Tanda waktu LRC dengan ketelitian seperseratus detik, misalnya [01:23.45].</summary>
    public static string Stamp(TimeSpan time) => $"[{Clock(time)}]";

    private static string Clock(TimeSpan time)
    {
        long centiseconds = (long)Math.Round(Math.Max(0, time.TotalMilliseconds) / 10);
        return $"{centiseconds / 6000:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}";
    }

    /// <summary>
    /// Teks baris dengan &lt;mm:ss.xx&gt; di depan setiap kata, dan satu lagi di akhir kalau waktu selesainya diketahui.
    /// Tanda disisipkan ke teks barisnya sendiri (bukan menyusun ulang dari kata), jadi spasinya tetap sama; kalau
    /// kata-katanya tidak cocok dengan teks baris, baris ditulis tanpa waktu per kata.
    /// </summary>
    private static string WithWordTags(LyricLine line)
    {
        if (line.Words.Count == 0)
            return line.Text;

        var result = new StringBuilder();
        int position = 0;
        foreach (var word in line.Words)
        {
            // Lewati spasi sebelum kata, lalu pastikan huruf-huruf kata ini memang ada di sini
            while (position < line.Text.Length && char.IsWhiteSpace(line.Text[position]))
                result.Append(line.Text[position++]);
            result.Append('<').Append(Clock(line.Time + word.Start)).Append('>');
            foreach (char c in word.Text)
            {
                if (char.IsWhiteSpace(c))
                    continue;
                while (position < line.Text.Length && char.IsWhiteSpace(line.Text[position]))
                    result.Append(line.Text[position++]);
                if (position >= line.Text.Length || line.Text[position] != c)
                    return line.Text;
                result.Append(line.Text[position++]);
            }
        }
        if (line.Text[position..].Trim().Length > 0)
            return line.Text;
        if (line.WordsEnd is { } end)
            result.Append('<').Append(Clock(line.Time + end)).Append('>');
        return result.ToString();
    }
}

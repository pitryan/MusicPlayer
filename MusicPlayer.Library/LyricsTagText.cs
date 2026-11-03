using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>
/// Teks tag LYRICS yang ditulis editor lirik. Baris editor berasal dari LrcParser, yang membuang baris info LRC
/// ([ti:], [ar:], [by:], [length:], ...) dan tanda waktu per kata (LRC "enhanced"); kalau baris itu ditulis ulang apa
/// adanya (LrcWriter), keduanya hilang dari file. Jadi tag LYRICS tidak ditulis ulang kalau liriknya tidak berubah, dan
/// kalau berubah, bagian-bagian itu diambil kembali dari teks asalnya.
/// </summary>
public static class LyricsTagText
{
    // Sama dengan daftar tag info di LrcParser
    private static readonly HashSet<string> InfoTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "al", "ti", "au", "by", "re", "ve", "la", "id", "length", "offset", "tool", "#",
    };

    // Tanda waktu di awal baris (bisa beberapa), dan tanda waktu per kata di tengah baris; polanya sama dengan LrcParser
    private static readonly Regex LeadingTimes = new(
        @"^(?:\[\s*\d{1,3}:\d{1,2}(?:[.:,]\d{1,3})?\s*\]\s*)+", RegexOptions.CultureInvariant);

    private static readonly Regex InlineTime = new(
        @"([<\[])(\d{1,3}):(\d{1,2})(?:[.:,](\d{1,3}))?([>\]])", RegexOptions.CultureInvariant);

    /// <summary>
    /// Teks yang ditulis ke tag LYRICS untuk baris <paramref name="edited"/> (lengkap dengan cara baca dan terjemahan
    /// yang ikut ditulis ke LYRICS_READING dan LYRICS_TRANSLATION), atau null kalau tag LYRICS tidak perlu ditulis
    /// ulang: <paramref name="original"/> sudah tersimpan di tag (<paramref name="originalInTag"/>) dan dibaca kembali
    /// menjadi baris yang sama persis, termasuk cara baca dan terjemahannya.
    /// </summary>
    /// <param name="original">Teks asal baris-baris itu apa adanya: isi tag LYRICS, file .lrc, atau teks yang ditempel.
    /// Null kalau tidak ada (misalnya lirik dari LRCLIB).</param>
    public static string? ForSave(Lyrics edited, string? original, bool originalInTag)
    {
        // Tanpa baris: lirik yang ada dihapus. Kalau dari awal tidak ada lirik yang terbaca, tidak ada yang perlu dihapus
        // (dan lirik yang gagal dibaca, misalnya karena file sedang dikunci, tidak ikut terhapus).
        if (edited.Lines.Count == 0)
            return original is null ? null : "";

        if (originalInTag && LrcParser.Parse(original) is { } stored)
        {
            // Dibaca seperti LyricsFinder membacanya: lirik dari tag, lalu cara baca dan terjemahan dari tag menurut urutan
            // baris. LRC tiga lapis di tag membawa lapisannya sendiri, yang tetap terbaca untuk baris yang lapisannya
            // dikosongkan di editor; dalam hal itu liriknya ditulis ulang (tanpa baris lapisan itu).
            string reading = LyricLayers.JoinLines(edited.Lines.Select(l => l.Reading));
            string translation = LyricLayers.JoinLines(edited.Lines.Select(l => l.Translation));
            var readBack = LyricLayers.Attach(stored, reading, translation);
            var expected = LyricLayers.Attach(WithoutLayers(edited), reading, translation);
            if (SameLines(readBack, expected))
                return null;
        }

        return Format(edited, original);
    }

    /// <summary>
    /// Seperti <see cref="LrcWriter.Format"/>, ditambah yang dibuang LrcParser dari <paramref name="original"/>: baris
    /// info LRC (di awal), dan tanda waktu per kata untuk baris yang waktu dan teksnya masih sama. [offset:] tidak ikut,
    /// karena sudah diterapkan ke tanda waktu baris; tanda waktu per katanya digeser sama banyak. Hasilnya selalu dibaca
    /// LrcParser menjadi baris yang sama dengan hasil LrcWriter.Format; kalau tidak, hasil LrcWriter.Format yang dipakai.
    /// </summary>
    public static string Format(Lyrics lyrics, string? original)
    {
        string plain = LrcWriter.Format(lyrics);
        if (string.IsNullOrWhiteSpace(original) || lyrics.Lines.Count == 0)
            return plain;

        var (header, words) = ReadOriginal(original);
        if (header.Count == 0 && words.Count == 0)
            return plain;

        var text = new StringBuilder();
        foreach (string line in header)
            text.Append(line).Append('\n');
        foreach (var line in lyrics.Lines)
        {
            string prefix = lyrics.IsSynced ? LrcWriter.Stamp(line.Time) : "";
            foreach (string part in line.Extra.Prepend(line.Text))
            {
                string body = lyrics.IsSynced && words.TryGetValue((line.Time, part), out string? timed) ? timed : part;
                text.Append(prefix).Append(body).Append('\n');
            }
        }

        string merged = text.ToString().TrimEnd('\n');
        return SameLines(LrcParser.Parse(merged), LrcParser.Parse(plain)) ? merged : plain;
    }

    /// <summary>
    /// Baris info LRC dari teks asal (kecuali [offset:]), dan isi baris yang bertanda waktu per kata (tanpa tanda waktu
    /// barisnya), menurut waktu baris (offset sudah diterapkan, seperti LrcParser) dan teks barisnya.
    /// </summary>
    private static (List<string> Header, Dictionary<(TimeSpan, string), string> Words) ReadOriginal(string original)
    {
        var header = new List<string>();
        var timed = new List<(TimeSpan Time, string Text, string Body)>();
        long offsetMs = 0;
        foreach (string raw in LyricLayers.SplitLines(original))
        {
            string line = raw.Trim().TrimStart('﻿').Trim();
            if (Info(line) is { } info)
            {
                if (!info.Name.Equals("offset", StringComparison.OrdinalIgnoreCase))
                    header.Add(line);
                else if (long.TryParse(info.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ms))
                    offsetMs = ms;
                continue;
            }

            // Hanya baris dengan satu tanda waktu: tanda waktu per kata di baris reff yang ditulis sekali untuk beberapa
            // waktu ([00:45.00][01:50.00]...) hanya benar untuk salah satunya
            var leading = LeadingTimes.Match(line);
            if (!leading.Success || !InlineTime.IsMatch(line, leading.Length))
                continue;
            if (LrcParser.Parse(line) is { IsSynced: true, Lines: [var parsed] } && !parsed.IsBlank)
                timed.Add((parsed.Time, parsed.Text, line[leading.Length..]));
        }

        // Menurut format LRC, offset positif berarti lirik tampil LEBIH AWAL (sama dengan LrcParser)
        var offset = TimeSpan.FromMilliseconds(offsetMs);
        var words = new Dictionary<(TimeSpan, string), string>();
        foreach (var (time, text, body) in timed)
        {
            var shifted = time - offset < TimeSpan.Zero ? TimeSpan.Zero : time - offset;
            words.TryAdd((shifted, text), offsetMs == 0 ? body : Shift(body, offset));
        }
        return (header, words);
    }

    /// <summary>Nama dan isi tag info kalau baris ini baris info LRC menurut aturan LrcParser, misalnya [ar:Artis].</summary>
    private static (string Name, string Value)? Info(string line)
    {
        if (!line.StartsWith('['))
            return null;
        int close = line.IndexOf(']');
        if (close < 0)
            return null;

        string tag = line[1..close].Trim();
        int colon = tag.IndexOf(':');
        if (colon <= 0 || !InfoTags.Contains(tag[..colon].Trim()))
            return null;
        return (tag[..colon].Trim(), tag[(colon + 1)..]);
    }

    // Tanda waktu per kata digeser seperti tanda waktu barisnya, dengan ketelitian yang sama (seperseratus detik)
    private static string Shift(string body, TimeSpan offset) => InlineTime.Replace(body, match =>
    {
        int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (seconds >= 60)
            return match.Value;

        int milliseconds = 0;
        if (match.Groups[4].Success)
        {
            string fraction = match.Groups[4].Value;
            milliseconds = int.Parse(fraction, CultureInfo.InvariantCulture) * (fraction.Length switch { 1 => 100, 2 => 10, _ => 1 });
        }

        var time = TimeSpan.FromMilliseconds((minutes * 60L + seconds) * 1000 + milliseconds) - offset;
        string stamp = LrcWriter.Stamp(time); // "[mm:ss.xx]", waktu negatif menjadi nol
        return match.Groups[1].Value + stamp[1..^1] + match.Groups[5].Value;
    });

    private static Lyrics WithoutLayers(Lyrics lyrics) =>
        new(lyrics.Lines.Select(l => l with { Reading = null, Translation = null }).ToList(), lyrics.IsSynced);

    private static bool SameLines(Lyrics? a, Lyrics? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.IsSynced != b.IsSynced || a.Lines.Count != b.Lines.Count)
            return false;

        for (int i = 0; i < a.Lines.Count; i++)
        {
            LyricLine x = a.Lines[i], y = b.Lines[i];
            if (x.Time != y.Time || x.Text != y.Text || x.Reading != y.Reading || x.Translation != y.Translation
                || !x.Extra.SequenceEqual(y.Extra))
                return false;
        }
        return true;
    }
}

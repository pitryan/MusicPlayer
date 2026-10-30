using System.Globalization;
using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>
/// Membaca lirik format LRC ("[01:23.45]baris lirik") atau teks biasa. Tag lagu dan file .lrc sama-sama bisa berisi
/// keduanya (banyak aplikasi menyimpan teks LRC utuh di tag LYRICS), jadi formatnya ditebak dari isinya.
/// </summary>
public static class LrcParser
{
    // [mm:ss], [mm:ss.x], [mm:ss.xx], atau [mm:ss.xxx]. Pemisah pecahan detik kadang ditulis ":" atau ",".
    private static readonly Regex TimeTag = new(
        @"^(\d{1,3}):(\d{1,2})(?:[.:,](\d{1,3}))?$", RegexOptions.CultureInvariant);

    // Tanda waktu per kata di tengah baris (LRC "enhanced"): <01:23.45> atau [01:23.45]. Dibuang dari teks barisnya,
    // dan dibaca sebagai waktu per kata untuk karaoke (ParseWords).
    private static readonly Regex InlineTime = new(
        @"[<\[]\d{1,3}:\d{1,2}(?:[.:,]\d{1,3})?[>\]]", RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedSpaces = new(@"[ \t]{2,}", RegexOptions.CultureInvariant);

    // Tag info LRC seperti [ar:Artis] dan [offset:+250]. Hanya nama-nama ini yang dianggap info, supaya baris lirik
    // seperti "[Intro: Drake]" atau "[Chorus]" tetap tampil.
    private static readonly HashSet<string> InfoTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "al", "ti", "au", "by", "re", "ve", "la", "id", "length", "offset", "tool", "#",
    };

    // Batas wajar, supaya file aneh (misalnya file biner bernama .lrc) tidak menghasilkan ribuan baris
    private const int MaxLines = 5000;

    /// <summary>Null kalau teksnya kosong atau tidak berisi lirik sama sekali.</summary>
    public static Lyrics? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var timed = new List<(TimeSpan Time, int Order, Entry Entry)>();
        var allTexts = new List<string>(); // urutan asli semua baris, untuk lirik yang ternyata bukan LRC
        int untimedWithText = 0;
        long offsetMs = 0;

        string[] rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string raw in rawLines.Take(MaxLines))
        {
            string line = raw.Trim().TrimStart('\uFEFF').Trim();
            var times = new List<TimeSpan>();
            bool isInfo = false;

            // Satu baris bisa diawali beberapa tanda waktu sekaligus, misalnya reff: [00:45.00][01:50.00]teks
            while (line.StartsWith('['))
            {
                int close = line.IndexOf(']');
                if (close < 0)
                    break;

                string tag = line[1..close].Trim();
                if (TryParseTime(tag, out var time))
                {
                    times.Add(time);
                    line = line[(close + 1)..].TrimStart();
                    continue;
                }

                if (times.Count == 0 && TryParseInfo(tag, out string name, out string value))
                {
                    isInfo = true;
                    if (name.Equals("offset", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ms))
                        offsetMs = ms;
                }
                break;
            }

            if (isInfo)
                continue;

            string cleaned = Clean(line);
            allTexts.Add(cleaned);

            if (times.Count == 0)
            {
                if (cleaned.Length > 0)
                    untimedWithText++;
                continue;
            }

            // Waktu per kata hanya untuk baris dengan satu tanda waktu; baris reff yang ditulis sekali untuk beberapa
            // waktu ([00:45.00][01:50.00]teks) tidak bisa memakai waktu kata yang sama di setiap pengulangannya
            var (words, wordsEnd) = times.Count == 1 ? ParseWords(line, times[0]) : ([], null);
            foreach (var time in times)
                timed.Add((time, timed.Count, new Entry(cleaned, words, wordsEnd)));
        }

        // LRC kalau sebagian besar baris bertanda waktu. Baris tanpa tanda waktu di file LRC (misalnya judul atau
        // keterangan penulis lirik) diabaikan, sama seperti pemutar lain.
        int timedWithText = timed.Count(t => t.Entry.Text.Length > 0);
        return timedWithText > 0 && timedWithText >= untimedWithText
            ? BuildSynced(timed, offsetMs)
            : BuildPlain(allTexts);
    }

    private static Lyrics BuildSynced(List<(TimeSpan Time, int Order, Entry Entry)> timed, long offsetMs)
    {
        // Menurut format LRC, offset positif berarti lirik tampil LEBIH AWAL. Waktu per kata relatif terhadap barisnya,
        // jadi ikut bergeser bersama barisnya; hanya kalau baris tertahan di 0:00, katanya dimajukan sebanyak itu.
        var offset = TimeSpan.FromMilliseconds(offsetMs);
        var sorted = timed
            .Select(t => t.Time - offset < TimeSpan.Zero
                ? (Time: TimeSpan.Zero, t.Order, Entry: Shift(t.Entry, offset - t.Time))
                : (Time: t.Time - offset, t.Order, t.Entry))
            .OrderBy(t => t.Time)
            .ThenBy(t => t.Order); // OrderBy stabil: baris dengan waktu sama tetap berurutan seperti di file

        // Baris dengan waktu sama dikelompokkan: baris pertama lirik aslinya, sisanya baris tambahan (cara baca atau
        // terjemahan) yang ditulis tepat di bawahnya
        var groups = new List<(TimeSpan Time, List<Entry> Entries)>();
        foreach (var (time, _, entry) in sorted)
        {
            if (groups.Count > 0 && groups[^1].Time == time)
                groups[^1].Entries.Add(entry);
            else
                groups.Add((time, [entry]));
        }

        bool layered = IsLayered(groups);
        var lines = new List<LyricLine>();
        foreach (var (time, entries) in groups)
        {
            var first = entries[0];
            if (layered && entries.Count == 3 && first.Text.Length > 0)
            {
                lines.Add(WithWords(new LyricLine(time, first.Text)
                {
                    Reading = Layer(entries[1].Text, first.Text),
                    Translation = Layer(entries[2].Text, first.Text),
                }, first));
                continue;
            }

            var line = WithWords(new LyricLine(time, first.Text), first);
            foreach (var entry in entries.Skip(1))
            {
                string text = entry.Text;
                if (text.Length == 0 || text == line.Text || line.Extra.Contains(text))
                    continue;
                line = line.IsBlank ? WithWords(line with { Text = text }, entry) : line with { Extra = [.. line.Extra, text] };
            }
            lines.Add(line);
        }

        return new Lyrics(lines, isSynced: true);
    }

    /// <summary>Satu baris bertanda waktu dari file: teksnya (tanpa tanda waktu kata) dan waktu per katanya.</summary>
    private sealed record Entry(string Text, IReadOnlyList<LyricWord> Words, TimeSpan? WordsEnd);

    private static LyricLine WithWords(LyricLine line, Entry entry) =>
        line with { Words = entry.Words, WordsEnd = entry.WordsEnd };

    /// <summary>Waktu per kata dimajukan <paramref name="by"/> (tidak sampai sebelum barisnya).</summary>
    private static Entry Shift(Entry entry, TimeSpan by)
    {
        if (entry.Words.Count == 0)
            return entry;
        static TimeSpan Earlier(TimeSpan time, TimeSpan by) => time > by ? time - by : TimeSpan.Zero;
        return entry with
        {
            Words = [.. entry.Words.Select(w => w with { Start = Earlier(w.Start, by) })],
            WordsEnd = entry.WordsEnd is { } end ? Earlier(end, by) : null,
        };
    }

    /// <summary>
    /// Waktu per kata dari Enhanced LRC: setiap &lt;mm:ss.xx&gt; menandai mulainya teks sesudahnya, dan tanda terakhir
    /// (tanpa teks sesudahnya) menandai selesainya kata terakhir. Teks sebelum tanda pertama dimulai bersama barisnya.
    /// Dua tanda berturut-turut di tengah baris berarti jeda: kata sebelumnya selesai di tanda pertama, dan jedanya
    /// dicatat sebagai kata tanpa teks. Waktunya disimpan relatif terhadap baris. Kosong kalau barisnya tanpa tanda waktu
    /// kata atau waktunya mundur.
    /// </summary>
    private static (IReadOnlyList<LyricWord> Words, TimeSpan? End) ParseWords(string line, TimeSpan lineTime)
    {
        var matches = InlineTime.Matches(line);
        if (matches.Count == 0)
            return ([], null);

        var words = new List<LyricWord>();
        var start = TimeSpan.Zero;
        int position = 0;
        foreach (Match match in matches)
        {
            AddWord(line[position..match.Index], gapAllowed: true);
            if (!TryParseTime(match.Value[1..^1], out var time))
                return ([], null);
            start = time > lineTime ? time - lineTime : TimeSpan.Zero;
            position = match.Index + match.Length;
        }

        bool endTagged = line[position..].Trim().Length == 0;
        AddWord(line[position..], gapAllowed: false);

        // Jeda yang tidak memisahkan apa pun (waktunya sama dengan kata di sebelahnya, atau di akhir baris) dibuang
        for (int i = words.Count - 1; i >= 0; i--)
        {
            if (words[i].Text.Length == 0
                && (i == words.Count - 1 || words[i + 1].Start <= words[i].Start || i == 0 || words[i - 1].Start >= words[i].Start))
                words.RemoveAt(i);
        }
        if (words.Count == 0)
            return ([], null);
        for (int i = 1; i < words.Count; i++)
        {
            if (words[i].Start < words[i - 1].Start)
                return ([], null);
        }
        return (words, endTagged && start >= words[^1].Start ? start : null);

        void AddWord(string text, bool gapAllowed)
        {
            text = RepeatedSpaces.Replace(text, " ").Trim();
            if (text.Length > 0 || (gapAllowed && words.Count > 0))
                words.Add(new LyricWord(start, text));
        }
    }

    /// <summary>
    /// True untuk LRC tiga lapis: hampir setiap baris lirik diikuti tepat dua baris dengan waktu yang sama, yaitu cara
    /// baca lalu terjemahan (misalnya [00:02.35]もうちょっとだから, [00:02.35]Mou chotto dakara, [00:02.35]Karena
    /// tinggal sedikit lagi). Lapisan itu lalu bisa disembunyikan dari panel lirik seperti yang ditulis di editor lirik.
    /// LRC dua lapis tetap dibaca sebagai baris tambahan biasa, karena baris keduanya bisa cara baca atau terjemahan.
    /// </summary>
    private static bool IsLayered(List<(TimeSpan Time, List<Entry> Entries)> groups)
    {
        var withText = groups.Where(g => g.Entries[0].Text.Length > 0).ToList();
        int threes = withText.Count(g => g.Entries.Count == 3);
        return threes > 0 && withText.All(g => g.Entries.Count <= 3) && threes * 4 >= withText.Count * 3;
    }

    // Lapisan yang sama dengan lirik aslinya (misalnya baris bahasa Inggris "Light, light") tidak perlu ditulis ulang
    private static string? Layer(string text, string original) =>
        text.Length == 0 || string.Equals(text, original, StringComparison.OrdinalIgnoreCase) ? null : text;

    private static Lyrics? BuildPlain(List<string> texts)
    {
        var lines = new List<LyricLine>();
        foreach (string text in texts)
        {
            // Baris kosong di awal dan baris kosong beruntun dibuang; satu baris kosong cukup sebagai pemisah bait
            if (text.Length == 0 && (lines.Count == 0 || lines[^1].IsBlank))
                continue;

            lines.Add(new LyricLine(TimeSpan.Zero, text));
        }

        while (lines.Count > 0 && lines[^1].IsBlank)
            lines.RemoveAt(lines.Count - 1);

        return lines.Count > 0 ? new Lyrics(lines, isSynced: false) : null;
    }

    private static string Clean(string line)
    {
        if (line.Contains('<') || line.Contains('['))
            line = InlineTime.Replace(line, "");

        return RepeatedSpaces.Replace(line, " ").Trim();
    }

    private static bool TryParseTime(string tag, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var match = TimeTag.Match(tag);
        if (!match.Success)
            return false;

        int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (seconds >= 60)
            return false;

        // Pecahan detik: "4" = 400 ms, "45" = 450 ms, "456" = 456 ms
        int milliseconds = 0;
        if (match.Groups[3].Success)
        {
            string fraction = match.Groups[3].Value;
            milliseconds = int.Parse(fraction, CultureInfo.InvariantCulture) * (fraction.Length switch { 1 => 100, 2 => 10, _ => 1 });
        }

        time = TimeSpan.FromMilliseconds((minutes * 60L + seconds) * 1000 + milliseconds);
        return true;
    }

    private static bool TryParseInfo(string tag, out string name, out string value)
    {
        int colon = tag.IndexOf(':');
        name = colon > 0 ? tag[..colon].Trim() : "";
        value = colon > 0 ? tag[(colon + 1)..] : "";
        return colon > 0 && InfoTags.Contains(name);
    }
}

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

    // Tanda waktu per kata di tengah baris (LRC "enhanced"): <01:23.45> atau [01:23.45]. Dibuang, karena yang
    // disorot adalah satu baris utuh.
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

        var timed = new List<(TimeSpan Time, int Order, string Text)>();
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

            foreach (var time in times)
                timed.Add((time, timed.Count, cleaned));
        }

        // LRC kalau sebagian besar baris bertanda waktu. Baris tanpa tanda waktu di file LRC (misalnya judul atau
        // keterangan penulis lirik) diabaikan, sama seperti pemutar lain.
        int timedWithText = timed.Count(t => t.Text.Length > 0);
        return timedWithText > 0 && timedWithText >= untimedWithText
            ? BuildSynced(timed, offsetMs)
            : BuildPlain(allTexts);
    }

    private static Lyrics BuildSynced(List<(TimeSpan Time, int Order, string Text)> timed, long offsetMs)
    {
        // Menurut format LRC, offset positif berarti lirik tampil LEBIH AWAL
        var offset = TimeSpan.FromMilliseconds(offsetMs);
        var sorted = timed
            .Select(t => (Time: t.Time - offset < TimeSpan.Zero ? TimeSpan.Zero : t.Time - offset, t.Order, t.Text))
            .OrderBy(t => t.Time)
            .ThenBy(t => t.Order); // OrderBy stabil: baris dengan waktu sama tetap berurutan seperti di file

        var lines = new List<LyricLine>();
        foreach (var (time, _, text) in sorted)
        {
            if (lines.Count > 0 && lines[^1].Time == time)
            {
                // Waktu sama dengan baris sebelumnya: baris tambahan (terjemahan atau cara baca), bukan baris baru
                var last = lines[^1];
                if (text.Length == 0 || text == last.Text || last.Extra.Contains(text))
                    continue;

                lines[^1] = last.IsBlank ? last with { Text = text } : last with { Extra = [.. last.Extra, text] };
                continue;
            }

            lines.Add(new LyricLine(time, text));
        }

        return new Lyrics(lines, isSynced: true);
    }

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

using System.Text;

namespace MusicPlayer.Library;

/// <summary>
/// Menulis lirik kembali sebagai teks untuk tag LYRICS: LRC bertanda waktu ([mm:ss.xx]) kalau liriknya bertanda waktu,
/// teks biasa kalau tidak. Baris tambahan dengan waktu yang sama (Extra) ikut ditulis, jadi LRC dwibahasa tetap utuh.
/// Hasilnya dibaca kembali LrcParser menjadi baris yang sama.
/// </summary>
public static class LrcWriter
{
    public static string Format(Lyrics lyrics)
    {
        var text = new StringBuilder();
        foreach (var line in lyrics.Lines)
        {
            string prefix = lyrics.IsSynced ? Stamp(line.Time) : "";
            text.Append(prefix).Append(line.Text).Append('\n');
            foreach (string extra in line.Extra)
                text.Append(prefix).Append(extra).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Tanda waktu LRC dengan ketelitian seperseratus detik, misalnya [01:23.45].</summary>
    public static string Stamp(TimeSpan time)
    {
        long centiseconds = (long)Math.Round(Math.Max(0, time.TotalMilliseconds) / 10);
        return $"[{centiseconds / 6000:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}]";
    }
}

namespace MusicPlayer.Library;

/// <summary>Kredit penulis lagu di bawah lirik, seperti Apple Music: "Written by: Penulis Lirik, Komposer".</summary>
public static class SongCredits
{
    /// <summary>
    /// Penulis lirik lalu komposer (tag LYRICIST dan COMPOSER, keduanya bisa berisi beberapa nama), tanpa nama ganda.
    /// Null kalau keduanya kosong.
    /// </summary>
    public static string? WrittenBy(string? lyricist, string? composer)
    {
        var names = new List<string>();
        foreach (string? value in new[] { lyricist, composer })
        {
            foreach (string name in TagFields.Split(value ?? "", multiValue: true))
            {
                string trimmed = name.Trim();
                if (trimmed.Length > 0 && !names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    names.Add(trimmed);
            }
        }
        return names.Count == 0 ? null : "Written by: " + string.Join(", ", names);
    }
}

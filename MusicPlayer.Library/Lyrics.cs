namespace MusicPlayer.Library;

/// <summary>Asal lirik, untuk keterangan kecil di bawah lirik.</summary>
public enum LyricsSource
{
    /// <summary>Tag di file lagu (LYRICS, UNSYNCEDLYRICS, USLT, atau ©lyr).</summary>
    Tag,

    /// <summary>File .lrc bernama sama di sebelah file lagu.</summary>
    LrcFile,

    /// <summary>LRCLIB (lrclib.net). Hasilnya disimpan lokal, jadi lagu yang sama tidak butuh internet lagi.</summary>
    Lrclib,
}

/// <summary>
/// Satu baris lirik. Untuk lirik tanpa tanda waktu, <see cref="Time"/> selalu nol. Baris kosong berarti jeda:
/// jeda instrumental di lirik bertanda waktu, atau pemisah bait di lirik biasa.
/// </summary>
public sealed record LyricLine(TimeSpan Time, string Text)
{
    /// <summary>
    /// Baris lain dengan tanda waktu yang sama. File LRC kadang menulis terjemahan atau cara baca tepat di bawah
    /// baris aslinya dengan waktu yang sama; baris itu tampil di bawahnya dengan huruf lebih kecil.
    /// </summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>Cara baca baris ini (romaji, romanisasi, pinyin), ditulis manual di editor lirik.</summary>
    public string? Reading { get; init; }

    /// <summary>Terjemahan baris ini, ditulis manual di editor lirik.</summary>
    public string? Translation { get; init; }

    /// <summary>
    /// Waktu per kata dari Enhanced LRC ("[00:02.35]&lt;00:02.35&gt;もう&lt;00:02.80&gt;ちょっと..."), untuk karaoke yang
    /// sinkron. Kosong kalau barisnya hanya punya waktu per baris; teks semua kata, tanpa spasi, sama dengan
    /// <see cref="Text"/> tanpa spasi.
    /// </summary>
    public IReadOnlyList<LyricWord> Words { get; init; } = [];

    /// <summary>Kapan kata terakhir selesai, relatif terhadap <see cref="Time"/>; null kalau file tidak menuliskannya.</summary>
    public TimeSpan? WordsEnd { get; init; }

    public bool IsBlank => Text.Length == 0;
}

/// <summary>Satu kata (atau huruf, untuk bahasa Jepang) dan kapan mulai dinyanyikan, relatif terhadap waktu barisnya.</summary>
public sealed record LyricWord(TimeSpan Start, string Text);

/// <summary>Lirik sebuah lagu: bertanda waktu (bisa disorot per baris) atau teks biasa.</summary>
public sealed class Lyrics
{
    public Lyrics(IReadOnlyList<LyricLine> lines, bool isSynced)
    {
        Lines = lines;
        IsSynced = isSynced;
    }

    /// <summary>Baris-baris lirik. Untuk lirik bertanda waktu, selalu urut dari waktu paling awal.</summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>True kalau setiap baris punya tanda waktu, sehingga baris yang sedang dinyanyikan bisa disorot.</summary>
    public bool IsSynced { get; }

    /// <summary>
    /// Baris yang sedang dinyanyikan pada posisi tertentu: baris terakhir yang waktunya sudah lewat.
    /// Hasilnya -1 sebelum baris pertama, dan selalu -1 untuk lirik tanpa tanda waktu.
    /// </summary>
    public int IndexAt(TimeSpan position)
    {
        if (!IsSynced || Lines.Count == 0 || position < Lines[0].Time)
            return -1;

        // Pencarian biner, karena dipanggil 10 kali per detik selama panel lirik terbuka
        int low = 0, high = Lines.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Lines[middle].Time <= position)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }

    /// <summary>
    /// Versi untuk ditampilkan: baris yang punya waktu per kata dimulai saat kata pertamanya dinyanyikan. Tanda waktu
    /// baris di banyak file LRC sedikit terlalu awal (sampai lebih dari satu detik), jadi tanpa ini baris berikutnya sudah
    /// menyala sementara baris ini masih dinyanyikan. Baris tidak dimundurkan melewati awal baris sesudahnya, dan waktu
    /// katanya ikut disesuaikan supaya tetap jatuh di waktu yang sama.
    /// </summary>
    public Lyrics WithWordStarts()
    {
        if (!IsSynced || Lines.All(l => l.Words.Count == 0))
            return this;

        var lines = Lines.ToArray();
        var limit = TimeSpan.MaxValue;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            var start = line.Words.Count > 0 ? line.Time + line.Words[0].Start : line.Time;
            if (start > limit)
                start = limit;
            limit = start;
            if (start == line.Time)
                continue;

            var shift = start - line.Time;
            lines[i] = line with
            {
                Time = start,
                Words = [.. line.Words.Select(w => w with { Start = w.Start > shift ? w.Start - shift : TimeSpan.Zero })],
                WordsEnd = line.WordsEnd is { } end ? (end > shift ? end - shift : TimeSpan.Zero) : null,
            };
        }
        return new Lyrics(lines, isSynced: true);
    }

    /// <summary>
    /// Versi untuk ditampilkan. Jeda instrumental yang lebih pendek dari <paramref name="minimumGap"/> dibuang,
    /// supaya tanda "• • •" tidak muncul di setiap tarikan napas; baris sebelumnya tetap menyala sampai baris
    /// berikutnya. Jeda yang panjang tetap ada, dan intro yang panjang mendapat baris jeda di 0:00, seperti Apple Music.
    /// Lirik tanpa tanda waktu dikembalikan apa adanya.
    /// </summary>
    public Lyrics WithInterludes(TimeSpan minimumGap)
    {
        if (!IsSynced || Lines.Count == 0)
            return this;

        var result = new List<LyricLine>(Lines.Count + 1);
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (!line.IsBlank)
            {
                result.Add(line);
                continue;
            }

            // Jeda di akhir lagu (tidak ada baris lagi sesudahnya) dan jeda beruntun tidak perlu ditampilkan
            var next = Lines.Skip(i + 1).FirstOrDefault(l => !l.IsBlank);
            if (next is null || (result.Count > 0 && result[^1].IsBlank) || next.Time - line.Time < minimumGap)
                continue;

            // Jeda sebelum baris pertama adalah intro, jadi dimulai dari awal lagu
            result.Add(result.Count == 0 ? line with { Time = TimeSpan.Zero } : line);
        }

        if (result.Count > 0 && !result[0].IsBlank && result[0].Time >= minimumGap)
            result.Insert(0, new LyricLine(TimeSpan.Zero, ""));

        return new Lyrics(result, isSynced: true);
    }
}

/// <summary>Lirik yang ditemukan, beserta asalnya. <see cref="FileName"/> diisi untuk file .lrc.</summary>
public sealed record FoundLyrics(Lyrics Lyrics, LyricsSource Source, string? FileName = null);

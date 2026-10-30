using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>Hasil mencari di LRCLIB (atau cache-nya).</summary>
public enum OnlineStatus
{
    Found,
    Instrumental,
    NotFound,

    /// <summary>LRCLIB tidak bisa dihubungi (offline, timeout, server error). Tidak disimpan di cache.</summary>
    Failed,

    /// <summary>Tidak dicari: judul atau artis lagu kosong, atau pencarian online dimatikan.</summary>
    Skipped,
}

public sealed record OnlineLookup(OnlineStatus Status, FoundLyrics? Lyrics = null, string? Error = null);

/// <summary>Kesimpulan akhir untuk satu lagu.</summary>
public enum LyricsOutcome
{
    Found,
    NotFound,
    Instrumental,

    /// <summary>Tidak ada lirik lokal, dan LRCLIB tidak bisa dihubungi.</summary>
    Unavailable,
}

public sealed record LyricsChoice(LyricsOutcome Outcome, FoundLyrics? Lyrics = null, string? Error = null);

/// <summary>
/// Mencari lirik sebuah lagu, berurutan: tag di file, file .lrc bernama sama di sebelahnya, lalu LRCLIB.
/// Satu aturan di atas urutan itu: lirik bertanda waktu selalu menang dari lirik teks biasa. Banyak file hanya
/// menyimpan lirik tanpa tanda waktu di tag-nya, jadi untuk lagu seperti itu LRCLIB tetap dicek; kalau di sana ada
/// versi bertanda waktu, versi itu yang dipakai, kalau tidak, lirik dari tag tetap dipakai.
/// </summary>
public sealed class LyricsFinder
{
    // Selisih durasi yang masih dianggap lagu yang sama. Lebih dari ini biasanya versi lain (TV size, live, remix).
    private const double DurationToleranceSeconds = 3;

    // Akhiran nama rilis ala Apple Music: "Engeki/Passion at 25:00 - Single". Di LRCLIB biasanya tanpa akhiran ini.
    private static readonly Regex ReleaseSuffix = new(
        @"\s+-\s+(Single|EP)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "(feat. …)", "[feat. …]", "（feat. …）" di judul lagu
    private static readonly Regex TitleFeaturing = new(
        @"\s*[\(\[（]\s*(feat\.?|ft\.?|featuring)\s[^\)\]）]*[\)\]）]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly LyricsCache? _cache;
    private readonly LrclibClient? _lrclib;

    /// <param name="cache">Tempat menyimpan hasil LRCLIB; null = tidak disimpan.</param>
    /// <param name="lrclib">Klien LRCLIB; null = hanya mencari di file lokal.</param>
    public LyricsFinder(LyricsCache? cache, LrclibClient? lrclib)
    {
        _cache = cache;
        _lrclib = lrclib;
    }

    /// <summary>
    /// Lirik dari file lokal: tag dulu, lalu file .lrc. Yang bertanda waktu didahulukan, jadi file .lrc menang dari
    /// tag yang hanya berisi teks biasa. Membaca disk, jadi panggil di luar UI thread.
    /// </summary>
    public FoundLyrics? FindLocal(Track track)
    {
        var fromTag = WithLayers(LrcParser.Parse(TagReader.ReadLyrics(track.FilePath)), track.FilePath);
        if (fromTag is { IsSynced: true })
            return new FoundLyrics(fromTag, LyricsSource.Tag);

        string? lrcPath = FindLrcFile(track.FilePath);
        var fromFile = lrcPath is null ? null : LrcParser.Parse(TextFiles.ReadSmallText(lrcPath));
        if (fromFile is { IsSynced: true })
        {
            // Cara baca dan terjemahan tersimpan bersama lirik di tag; kalau file .lrc yang menang, lapisannya
            // dipindahkan ke baris .lrc yang teksnya sama, supaya tidak hilang
            if (fromTag is not null)
                fromFile = LyricLayers.Transfer(fromTag, fromFile);
            return new FoundLyrics(fromFile, LyricsSource.LrcFile, Path.GetFileName(lrcPath));
        }

        if (fromTag is not null)
            return new FoundLyrics(fromTag, LyricsSource.Tag);
        if (fromFile is not null)
            return new FoundLyrics(fromFile, LyricsSource.LrcFile, Path.GetFileName(lrcPath));

        return null;
    }

    /// <summary>
    /// Cara baca dan terjemahan dari editor lirik (tag LYRICS_READING dan LYRICS_TRANSLATION) menempel ke lirik dari tag
    /// file yang sama; keduanya disimpan bersamaan oleh editor, jadi urutan barisnya cocok.
    /// </summary>
    private static Lyrics? WithLayers(Lyrics? lyrics, string path)
    {
        if (lyrics is null)
            return null;
        try
        {
            var tags = TagEditor.Read(path);
            return LyricLayers.Attach(lyrics, tags.Get("lyrics_reading"), tags.Get("lyrics_translation"));
        }
        catch (Exception)
        {
            return lyrics; // tag tidak bisa dibaca ulang: lirik tetap tampil tanpa lapisannya
        }
    }

    /// <summary>
    /// File .lrc bernama sama dengan lagunya, misalnya "01 トワイライトライト.lrc" untuk "01 トワイライトライト.flac".
    /// Kalau tidak ada yang sama persis, nama di folder itu dibandingkan setelah dirapikan: nama file yang pernah
    /// disalin dari Mac bisa menyimpan huruf seperti "ド" sebagai dua karakter (ト + tanda dakuten), sehingga terlihat
    /// sama tapi tidak sama persis.
    /// </summary>
    private static string? FindLrcFile(string trackPath)
    {
        try
        {
            string exact = Path.ChangeExtension(trackPath, ".lrc");
            if (File.Exists(exact))
                return exact;

            string? folder = Path.GetDirectoryName(trackPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return null;

            string wanted = Path.GetFileNameWithoutExtension(trackPath).Normalize(NormalizationForm.FormC);
            var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(folder, "*.lrc", options)
                .FirstOrDefault(f => string.Equals(
                    Path.GetFileNameWithoutExtension(f).Normalize(NormalizationForm.FormC), wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lirik dari LRCLIB: cache lokal dulu, baru internet. Hasilnya (termasuk "tidak ditemukan") disimpan di cache.
    /// Tidak pernah melempar exception kecuali dibatalkan lewat <paramref name="ct"/>.
    /// </summary>
    public async Task<OnlineLookup> FindOnlineAsync(Track track, CancellationToken ct)
    {
        string? title = track.Title?.Trim();
        string? artist = (track.Artist ?? track.AlbumArtist)?.Trim();

        // Tanpa judul dan artis dari tag, hasil pencarian terlalu sering salah lagu
        if (_lrclib is null || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
            return new OnlineLookup(OnlineStatus.Skipped);

        string album = ReleaseSuffix.Replace(track.Album ?? "", "").Trim();
        int duration = (int)Math.Round(track.Duration.TotalSeconds);
        string key = LyricsCache.KeyFor(title, artist, album, duration);

        if (_cache?.Get(key, DateTime.UtcNow) is { } cached)
            return FromCache(cached);

        LrclibTrack? match;
        try
        {
            match = await SearchLrclibAsync(_lrclib, title, artist, album, duration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // lagu sudah berganti
        }
        catch (OperationCanceledException)
        {
            return new OnlineLookup(OnlineStatus.Failed, Error: "LRCLIB took too long to answer.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return new OnlineLookup(OnlineStatus.Failed, Error: ex is HttpRequestException { StatusCode: { } code }
                ? $"LRCLIB answered with an error ({(int)code})."
                : "LRCLIB couldn't be reached.");
        }

        var entry = match switch
        {
            { HasLyrics: true } => new CachedLyrics(OnlineStatus.Found, match.SyncedLyrics, match.PlainLyrics, DateTime.UtcNow),
            { Instrumental: true } => new CachedLyrics(OnlineStatus.Instrumental, null, null, DateTime.UtcNow),
            _ => new CachedLyrics(OnlineStatus.NotFound, null, null, DateTime.UtcNow),
        };
        _cache?.Put(key, entry);
        return FromCache(entry);
    }

    /// <summary>
    /// Memilih lirik yang ditampilkan: lirik lokal bertanda waktu, lalu LRCLIB bertanda waktu, lalu lirik lokal teks
    /// biasa, lalu LRCLIB teks biasa. Kalau tidak ada sama sekali: instrumental, tidak bisa dicek, atau tidak ada.
    /// </summary>
    public static LyricsChoice Choose(FoundLyrics? local, OnlineLookup online)
    {
        if (local is { Lyrics.IsSynced: true })
            return new LyricsChoice(LyricsOutcome.Found, local);
        if (online is { Status: OnlineStatus.Found, Lyrics: { Lyrics.IsSynced: true } synced })
        {
            // Lirik lokal tanpa tanda waktu kalah dari LRCLIB, tapi cara baca dan terjemahannya (dari editor lirik)
            // tetap dibawa ke baris LRCLIB yang teksnya sama
            if (local is not null && LyricLayers.HasLayers(local.Lyrics))
                synced = synced with { Lyrics = LyricLayers.Transfer(local.Lyrics, synced.Lyrics) };
            return new LyricsChoice(LyricsOutcome.Found, synced);
        }
        if (local is not null)
            return new LyricsChoice(LyricsOutcome.Found, local);

        return online switch
        {
            { Status: OnlineStatus.Found, Lyrics: { } plain } => new LyricsChoice(LyricsOutcome.Found, plain),
            { Status: OnlineStatus.Instrumental } => new LyricsChoice(LyricsOutcome.Instrumental),
            { Status: OnlineStatus.Failed } => new LyricsChoice(LyricsOutcome.Unavailable, Error: online.Error),
            _ => new LyricsChoice(LyricsOutcome.NotFound),
        };
    }

    /// <summary>
    /// Paling banyak tiga permintaan: pencarian tepat (dengan album), pencarian judul dan artis (album di LRCLIB sering
    /// ditulis berbeda), lalu sekali lagi tanpa "(feat. …)". Berhenti begitu ketemu lirik bertanda waktu.
    /// </summary>
    private static async Task<LrclibTrack?> SearchLrclibAsync(
        LrclibClient lrclib, string title, string artist, string album, int duration, CancellationToken ct)
    {
        LrclibTrack? best = null;
        if (album.Length > 0 && duration > 0)
        {
            best = await lrclib.GetAsync(title, artist, album, duration, ct).ConfigureAwait(false);
            if (best is { HasSyncedLyrics: true } or { Instrumental: true, HasLyrics: false })
                return best;
        }

        best = Better(best, PickBest(await lrclib.SearchAsync(title, artist, ct).ConfigureAwait(false), duration));
        if (best is { HasSyncedLyrics: true })
            return best;

        string shortTitle = TitleFeaturing.Replace(title, "").Trim();
        string mainArtist = ArtistNames.Primary(artist);
        if (shortTitle.Length > 0 && (shortTitle != title || mainArtist != artist))
            best = Better(best, PickBest(await lrclib.SearchAsync(shortTitle, mainArtist, ct).ConfigureAwait(false), duration));

        return best;
    }

    /// <summary>Hasil pencarian yang durasinya cocok; bertanda waktu dulu, lalu yang durasinya paling dekat.</summary>
    private static LrclibTrack? PickBest(IReadOnlyList<LrclibTrack> results, int duration) => results
        .Where(r => r.HasLyrics || r.Instrumental)
        .Where(r => duration <= 0 || Math.Abs(r.Duration - duration) <= DurationToleranceSeconds)
        .OrderByDescending(Rank)
        .ThenBy(r => duration <= 0 ? 0 : Math.Abs(r.Duration - duration))
        .FirstOrDefault();

    private static LrclibTrack? Better(LrclibTrack? a, LrclibTrack? b) => Rank(b) > Rank(a) ? b : a;

    private static int Rank(LrclibTrack? track) => track switch
    {
        { HasSyncedLyrics: true } => 3,
        { HasLyrics: true } => 2,
        { Instrumental: true } => 1,
        _ => 0,
    };

    private static OnlineLookup FromCache(CachedLyrics entry)
    {
        if (entry.Status == OnlineStatus.Found)
        {
            // Teks bertanda waktu dipakai kalau benar-benar bisa dibaca sebagai LRC; selain itu teks biasanya
            var fromSynced = LrcParser.Parse(entry.SyncedLyrics);
            var lyrics = fromSynced is { IsSynced: true } ? fromSynced : LrcParser.Parse(entry.PlainLyrics) ?? fromSynced;
            return lyrics is null
                ? new OnlineLookup(OnlineStatus.NotFound)
                : new OnlineLookup(OnlineStatus.Found, new FoundLyrics(lyrics, LyricsSource.Lrclib));
        }

        return new OnlineLookup(entry.Status);
    }
}

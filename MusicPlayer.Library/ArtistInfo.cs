using System.Text.Json;

namespace MusicPlayer.Library;

/// <summary>
/// Keterangan artis untuk bagian "About" di halaman artis, seperti Apple Music. Dikumpulkan dari MusicBrainz (jenis,
/// asal, tanggal lahir atau terbentuk), Wikidata (judul artikel Wikipedia), dan Wikipedia (teks About), lalu disimpan
/// di database supaya cukup diambil sekali. Foto artis sengaja tidak diambil (Stage 14b): foto Wikimedia Commons
/// umumnya potret beresolusi kecil yang buram saat dipotong menjadi banner lebar, dan lisensinya mewajibkan nama
/// fotografer dicantumkan. Banner dan daftar artis memakai cover lokal.
/// </summary>
public sealed record ArtistInfo(string Name)
{
    public string? MusicBrainzId { get; init; }

    /// <summary>"Person", "Group", "Orchestra", ... dari MusicBrainz; menentukan label BORN atau FORMED.</summary>
    public string? Kind { get; init; }

    /// <summary>Bagian pembuka artikel Wikipedia bahasa Inggris, teks biasa.</summary>
    public string? About { get; init; }

    /// <summary>Alamat artikel Wikipedia-nya: teks Wikipedia berlisensi CC BY-SA, jadi sumbernya wajib ditautkan.</summary>
    public string? WikipediaUrl { get; init; }

    /// <summary>Kota dan negara asal, misalnya "Kumamoto, Japan".</summary>
    public string? Origin { get; init; }

    /// <summary>Tanggal lahir (orang) atau terbentuk (grup): "1990-07-09", "2009-05", atau "2009".</summary>
    public string? BeginDate { get; init; }

    public DateTime FetchedUtc { get; init; }

    /// <summary>False kalau artis ini sudah dicari tapi tidak ditemukan, supaya tidak dicari terus setiap halaman dibuka.</summary>
    public bool Found { get; init; }

    public bool IsGroup => Kind is "Group" or "Orchestra" or "Choir";

    public bool HasDetails => !string.IsNullOrWhiteSpace(About) || !string.IsNullOrWhiteSpace(Origin) || !string.IsNullOrWhiteSpace(BeginDate);

    // Data dianggap segar 30 hari; "tidak ditemukan" dicoba lagi setelah 7 hari (siapa tahu artikelnya sudah dibuat)
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);

    public bool IsFresh(DateTime nowUtc) => nowUtc - FetchedUtc < (Found ? FreshFor : RetryNotFoundAfter);
}

/// <summary>
/// Mengambil ArtistInfo dari internet: artis dicari di MusicBrainz (atau langsung lewat ID MusicBrainz dari tag
/// Picard, yang paling tepat), lalu Wikidata untuk judul artikel Wikipedia bahasa Inggris, lalu bagian pembuka artikel
/// itu. Semua layanan ini gratis dan tidak butuh kunci API.
/// </summary>
public sealed class ArtistInfoClient
{
    private const string UserAgent = "MusicPlayer/0.9 ( https://github.com/pitryan/MusicPlayer )";

    // Skor pencarian MusicBrainz minimal; di bawah ini hasilnya biasanya artis lain dengan nama mirip
    public const int MinimumScore = 90;

    private readonly MusicBrainzClient _musicBrainz;
    private readonly HttpClient _http;

    public ArtistInfoClient(MusicBrainzClient musicBrainz, HttpClient http)
    {
        _musicBrainz = musicBrainz;
        _http = http;
    }

    public async Task<ArtistInfo> FetchAsync(string name, string? musicBrainzId, CancellationToken ct)
    {
        string? id = string.IsNullOrWhiteSpace(musicBrainzId)
            ? PickArtist(name, await _musicBrainz.SearchArtistsAsync(name, ct).ConfigureAwait(false))?.Id
            : musicBrainzId.Trim();
        if (id is null)
            return new ArtistInfo(name) { FetchedUtc = DateTime.UtcNow, Found = false };

        var details = await _musicBrainz.GetArtistDetailsAsync(id, ct).ConfigureAwait(false);

        // Judul artikel Wikipedia bahasa Inggris: dari Wikidata (paling andal), atau dari tautan Wikipedia di MusicBrainz
        string? title = EnglishWikipediaTitle(details.WikipediaUrl);
        if (details.WikidataId is { Length: > 0 } wikidataId)
            title = await GetWikidataTitleAsync(wikidataId, ct).ConfigureAwait(false) ?? title;

        string? about = title is null ? null : await GetWikipediaIntroAsync(title, ct).ConfigureAwait(false);
        return new ArtistInfo(name)
        {
            MusicBrainzId = id,
            Kind = details.Type,
            Origin = Origin(details.BeginArea, details.Area),
            BeginDate = details.Begin,
            About = about,
            WikipediaUrl = title is null ? null : "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')),
            FetchedUtc = DateTime.UtcNow,
            Found = true,
        };
    }

    /// <summary>
    /// Hasil pencarian yang namanya (atau nama urut, atau salah satu aliasnya) persis sama dan skornya tinggi. Tanpa
    /// syarat ini, "Aimer" bisa tertukar dengan artis lain yang namanya hanya mirip.
    /// </summary>
    public static MbArtistMatch? PickArtist(string name, IReadOnlyList<MbArtistMatch> matches) =>
        matches
            .Where(m => m.Score >= MinimumScore
                && (Same(m.Name, name) || Same(m.SortName, name) || m.Aliases.Any(alias => Same(alias, name))))
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();

    /// <summary>"Kumamoto" + "Japan" → "Kumamoto, Japan"; kalau salah satunya tidak ada, yang ada saja.</summary>
    public static string? Origin(string? beginArea, string? area)
    {
        if (string.IsNullOrWhiteSpace(beginArea))
            return string.IsNullOrWhiteSpace(area) ? null : area;
        if (string.IsNullOrWhiteSpace(area) || Same(beginArea, area))
            return beginArea;
        return $"{beginArea}, {area}";
    }

    /// <summary>Judul artikel dari alamat Wikipedia bahasa Inggris; alamat bahasa lain diabaikan (teks aplikasi berbahasa Inggris).</summary>
    public static string? EnglishWikipediaTitle(string? url)
    {
        const string prefix = "https://en.wikipedia.org/wiki/";
        if (url is null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Uri.UnescapeDataString(url[prefix.Length..]).Replace('_', ' ');
    }

    /// <summary>Judul artikel Wikipedia bahasa Inggris dari entitas Wikidata, atau null kalau belum ada artikelnya.</summary>
    private async Task<string?> GetWikidataTitleAsync(string id, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://www.wikidata.org/wiki/Special:EntityData/{Uri.EscapeDataString(id)}.json", ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("entities", out var entities))
            return null;

        // Entitas yang sudah dialihkan muncul dengan ID barunya, jadi yang diambil entitas pertama
        foreach (var entity in entities.EnumerateObject())
        {
            return entity.Value.TryGetProperty("sitelinks", out var links)
                && links.TryGetProperty("enwiki", out var enwiki)
                && enwiki.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        return null;
    }

    private async Task<string?> GetWikipediaIntroAsync(string title, CancellationToken ct)
    {
        string url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&prop=extracts"
            + "&exintro=1&explaintext=1&redirects=1&titles=" + Uri.EscapeDataString(title);
        using var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("pages", out var pages)
            && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                if (page.TryGetProperty("extract", out var extract) && extract.GetString() is { Length: > 0 } text)
                    return text.Trim();
            }
        }
        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        // Wikimedia meminta User-Agent yang menyebut aplikasi dan kontaknya
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace MusicPlayer.Library;

public sealed record MbAlias(string Name, string SortName, string? Locale, bool Primary);

public sealed record MbArtist(string Id, string Name, string SortName, IReadOnlyList<MbAlias> Aliases);

/// <summary>Satu nama di kredit artis, misalnya "25時、ナイトコードで。" dengan joinphrase " × " sebelum nama berikutnya.</summary>
public sealed record MbCredit(string Name, string JoinPhrase, MbArtist Artist);

public sealed record MbTrack(string Id, int Position, string Number, string Title, int? LengthMs,
    IReadOnlyList<MbCredit> Credits, string RecordingId, IReadOnlyList<string> Isrcs);

public sealed record MbMedium(int Position, string? Format, string? Title, int TrackCount, IReadOnlyList<MbTrack> Tracks);

public sealed record MbLabel(string? Name, string? CatalogNumber);

/// <summary>
/// Satu rilis MusicBrainz. Hasil pencarian hanya berisi ringkasannya (media tanpa daftar lagu); lookup rilis berisi
/// semuanya. <see cref="Score"/> hanya ada di hasil pencarian (0–100).
/// </summary>
public sealed record MbRelease(
    string Id, string Title, IReadOnlyList<MbCredit> Credits, string? Date, string? Country, string? Status,
    string? Barcode, string? Asin, string? Language, string? Script, string? ReleaseGroupId, string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes, string? FirstReleaseDate, IReadOnlyList<MbLabel> Labels,
    IReadOnlyList<MbMedium> Media, bool HasFrontCover, int Score)
{
    public int TrackCount => Media.Sum(m => m.TrackCount);
}

/// <summary>
/// Klien web service MusicBrainz (https://musicbrainz.org/doc/MusicBrainz_API) dan Cover Art Archive. Tanpa kunci API,
/// tapi wajib mengikuti aturannya: paling banyak 1 permintaan per detik dan User-Agent yang menyebut aplikasi serta
/// kontaknya. Gagal tersambung dan jawaban error dilempar sebagai exception.
/// </summary>
public sealed class MusicBrainzClient
{
    private const string Api = "https://musicbrainz.org/ws/2/";
    private const string CoverArtArchive = "https://coverartarchive.org/release/";
    private const string UserAgent = "MusicPlayer/0.9 ( https://github.com/pitryan/MusicPlayer )";

    // Semua yang dibutuhkan untuk menulis tag seperti Picard, dalam satu permintaan
    private const string ReleaseIncludes = "artist-credits+recordings+release-groups+labels+isrcs+media+aliases";

    // Sedikit di atas 1 detik, supaya jam yang tidak presisi tidak membuat permintaan terlalu rapat
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public MusicBrainzClient(HttpClient http) => _http = http;

    /// <summary>Rilis yang cocok dengan judul album dan artis, yang paling cocok lebih dulu (paling banyak 25).</summary>
    public async Task<IReadOnlyList<MbRelease>> SearchReleasesAsync(string album, string artist, CancellationToken ct)
    {
        // Sintaks pencarian Lucene; tanda kutip membuat judul dicari sebagai frasa
        string query = $"release:\"{EscapeQuery(album)}\"";
        if (artist.Trim().Length > 0)
            query += $" AND artist:\"{EscapeQuery(artist)}\"";

        using var json = await GetJsonAsync($"{Api}release/?query={Uri.EscapeDataString(query)}&limit=25&fmt=json", ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return [];
        return releases.EnumerateArray().Select(ReadRelease).ToList();
    }

    /// <summary>Isi lengkap satu rilis: semua disc dan lagu, kredit, label, ISRC, dan alias artis.</summary>
    public async Task<MbRelease> GetReleaseAsync(string releaseId, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"{Api}release/{Uri.EscapeDataString(releaseId)}?inc={ReleaseIncludes}&fmt=json", ct).ConfigureAwait(false);
        return ReadRelease(json.RootElement);
    }

    /// <summary>Cover depan dari Cover Art Archive dalam ukuran 250, 500, atau 1200 piksel; null kalau tidak ada.</summary>
    public async Task<byte[]?> GetFrontCoverAsync(string releaseId, int size, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{CoverArtArchive}{Uri.EscapeDataString(releaseId)}/front-{size}");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        // MusicBrainz menjawab 503 kalau permintaan terlalu rapat; dicoba sekali lagi setelah jeda
        for (int attempt = 0; ; attempt++)
        {
            await WaitTurnAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 0)
                continue;

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Menunggu sampai boleh mengirim permintaan berikutnya (aturan 1 permintaan per detik).</summary>
    private async Task WaitTurnAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastRequestUtc + MinimumInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string EscapeQuery(string text) => text.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Membaca JSON (manual, supaya tetap jalan saat publish dengan trimming) ──

    private static MbRelease ReadRelease(JsonElement e)
    {
        var group = Object(e, "release-group");
        var text = Object(e, "text-representation");
        var media = Array(e, "media").Select(ReadMedium).ToList();
        return new MbRelease(
            Text(e, "id") ?? "",
            Text(e, "title") ?? "",
            ReadCredits(e),
            Text(e, "date"),
            Text(e, "country"),
            Text(e, "status"),
            Text(e, "barcode"),
            Text(e, "asin"),
            text is { } t ? Text(t, "language") : null,
            text is { } t2 ? Text(t2, "script") : null,
            group is { } g ? Text(g, "id") : null,
            group is { } g2 ? Text(g2, "primary-type") : null,
            group is { } g3 ? Array(g3, "secondary-types").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [],
            group is { } g4 ? Text(g4, "first-release-date") : null,
            Array(e, "label-info").Select(l => new MbLabel(Object(l, "label") is { } label ? Text(label, "name") : null, Text(l, "catalog-number"))).ToList(),
            media,
            Object(e, "cover-art-archive") is { } caa && caa.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True,
            e.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetInt32() : 0);
    }

    private static MbMedium ReadMedium(JsonElement m)
    {
        var tracks = Array(m, "tracks").Select(ReadTrack).ToList();
        int count = Number(m, "track-count") ?? tracks.Count;
        return new MbMedium(Number(m, "position") ?? 1, Text(m, "format"), Text(m, "title"), count, tracks);
    }

    private static MbTrack ReadTrack(JsonElement t)
    {
        var recording = Object(t, "recording");
        var credits = ReadCredits(t);
        // Kredit lagu kadang hanya ada di recording-nya
        if (credits.Count == 0 && recording is { } r)
            credits = ReadCredits(r);
        return new MbTrack(
            Text(t, "id") ?? "",
            Number(t, "position") ?? 0,
            Text(t, "number") ?? "",
            Text(t, "title") ?? (recording is { } r2 ? Text(r2, "title") : null) ?? "",
            Number(t, "length") ?? (recording is { } r3 ? Number(r3, "length") : null),
            credits,
            recording is { } r4 ? Text(r4, "id") ?? "" : "",
            recording is { } r5 ? Array(r5, "isrcs").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []);
    }

    private static List<MbCredit> ReadCredits(JsonElement e) =>
        Array(e, "artist-credit").Select(c =>
        {
            var artist = Object(c, "artist");
            var aliases = artist is { } a
                ? Array(a, "aliases").Select(x => new MbAlias(Text(x, "name") ?? "", Text(x, "sort-name") ?? "", Text(x, "locale"),
                    x.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)).ToList()
                : [];
            var mbArtist = new MbArtist(
                artist is { } a1 ? Text(a1, "id") ?? "" : "",
                artist is { } a2 ? Text(a2, "name") ?? "" : "",
                artist is { } a3 ? Text(a3, "sort-name") ?? "" : "",
                aliases);
            return new MbCredit(Text(c, "name") ?? mbArtist.Name, Text(c, "joinphrase") ?? "", mbArtist);
        }).ToList();

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static int? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    private static JsonElement? Object(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}

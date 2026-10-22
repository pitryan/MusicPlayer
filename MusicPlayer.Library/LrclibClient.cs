using System.Net;
using System.Text.Json;

namespace MusicPlayer.Library;

/// <summary>Satu entri lirik dari LRCLIB.</summary>
public sealed record LrclibTrack(
    long Id,
    string TrackName,
    string ArtistName,
    string? AlbumName,
    double Duration,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics)
{
    public bool HasSyncedLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasLyrics => HasSyncedLyrics || !string.IsNullOrWhiteSpace(PlainLyrics);
}

/// <summary>
/// Klien API LRCLIB (https://lrclib.net): database lirik gratis tanpa kunci API. Hanya membaca, tidak pernah mengunggah.
/// Gagal tersambung, timeout, dan jawaban yang tidak bisa dibaca dilempar sebagai exception ke pemanggil.
/// </summary>
public sealed class LrclibClient
{
    private const string ApiBase = "https://lrclib.net/api/";

    // LRCLIB meminta aplikasi memperkenalkan diri lewat User-Agent
    private const string UserAgent = "MusicPlayer/0.6 (personal music player for Windows; https://github.com/pitryan/MusicPlayer)";

    private readonly HttpClient _http;

    public LrclibClient(HttpClient http) => _http = http;

    /// <summary>
    /// Pencarian tepat dengan judul, artis, album, dan durasi (LRCLIB mencocokkan durasi ±2 detik).
    /// Null kalau tidak ada yang cocok.
    /// </summary>
    public async Task<LrclibTrack?> GetAsync(string title, string artist, string album, int durationSeconds, CancellationToken ct)
    {
        string url = $"{ApiBase}get?track_name={E(title)}&artist_name={E(artist)}&album_name={E(album)}&duration={durationSeconds}";
        using var json = await SendAsync(url, ct).ConfigureAwait(false);
        return json is null ? null : Read(json.RootElement);
    }

    /// <summary>Semua entri dengan judul dan artis itu (paling banyak 20, dari LRCLIB), dalam urutan dari LRCLIB.</summary>
    public async Task<IReadOnlyList<LrclibTrack>> SearchAsync(string title, string artist, CancellationToken ct)
    {
        string url = $"{ApiBase}search?track_name={E(title)}&artist_name={E(artist)}";
        using var json = await SendAsync(url, ct).ConfigureAwait(false);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        return json.RootElement.EnumerateArray().Select(Read).OfType<LrclibTrack>().ToList();
    }

    /// <summary>Null untuk 404 (tidak ditemukan). Status error lain dilempar sebagai HttpRequestException.</summary>
    private async Task<JsonDocument?> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    // JSON dibaca manual (bukan JsonSerializer) supaya tetap jalan saat aplikasi di-publish dengan trimming
    private static LrclibTrack? Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        string? Text(string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        double Number(string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

        bool instrumental = item.TryGetProperty("instrumental", out var flag) && flag.ValueKind == JsonValueKind.True;

        return new LrclibTrack(
            (long)Number("id"),
            Text("trackName") ?? "",
            Text("artistName") ?? "",
            Text("albumName"),
            Number("duration"),
            instrumental,
            Text("plainLyrics"),
            Text("syncedLyrics"));
    }

    private static string E(string value) => Uri.EscapeDataString(value.Trim());
}

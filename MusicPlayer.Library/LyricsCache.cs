using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicPlayer.Library;

/// <summary>Hasil pencarian LRCLIB yang disimpan: lirik, instrumental, atau tidak ditemukan.</summary>
public sealed record CachedLyrics(OnlineStatus Status, string? SyncedLyrics, string? PlainLyrics, DateTime FetchedUtc);

/// <summary>
/// Menyimpan hasil LRCLIB sebagai file kecil di folder data aplikasi, supaya lagu yang sama tidak butuh internet
/// lagi. Kuncinya dari judul, artis, album, dan durasi; kalau tag lagu diedit, liriknya dicari ulang.
/// </summary>
public sealed class LyricsCache
{
    /// <summary>Lagu yang tidak ditemukan dicari lagi setelah seminggu, siapa tahu sudah ada yang mengunggah liriknya.</summary>
    public static readonly TimeSpan NotFoundExpiry = TimeSpan.FromDays(7);

    private readonly string _folder;

    public LyricsCache(string folder) => _folder = folder;

    public static string KeyFor(string title, string artist, string album, int durationSeconds)
    {
        string signature = string.Join('\n',
            title.Trim().ToLowerInvariant(), artist.Trim().ToLowerInvariant(), album.Trim().ToLowerInvariant(), durationSeconds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..32].ToLowerInvariant();
    }

    /// <summary>Null kalau belum pernah dicari, sudah kedaluwarsa, atau file-nya rusak.</summary>
    public CachedLyrics? Get(string key, DateTime nowUtc)
    {
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path))
                return null;

            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            var status = root.GetProperty("status").GetString() switch
            {
                "found" => OnlineStatus.Found,
                "instrumental" => OnlineStatus.Instrumental,
                "notFound" => OnlineStatus.NotFound,
                _ => (OnlineStatus?)null,
            };
            if (status is null)
                return null;

            var fetched = root.GetProperty("fetchedUtc").GetDateTime().ToUniversalTime();
            if (status == OnlineStatus.NotFound && nowUtc - fetched > NotFoundExpiry)
                return null;

            return new CachedLyrics(status.Value, Text(root, "synced"), Text(root, "plain"), fetched);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null; // file rusak atau tidak terbaca: anggap belum pernah dicari
        }
    }

    /// <summary>Disimpan sebisanya: kalau gagal (misalnya disk penuh), lirik dicari lagi lain kali.</summary>
    public void Put(string key, CachedLyrics entry)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("status", entry.Status switch
                {
                    OnlineStatus.Found => "found",
                    OnlineStatus.Instrumental => "instrumental",
                    _ => "notFound",
                });
                writer.WriteString("fetchedUtc", entry.FetchedUtc.ToUniversalTime());
                if (entry.SyncedLyrics is not null)
                    writer.WriteString("synced", entry.SyncedLyrics);
                if (entry.PlainLyrics is not null)
                    writer.WriteString("plain", entry.PlainLyrics);
                writer.WriteEndObject();
            }

            // Ditulis ke file sementara dulu, supaya tidak ada file setengah jadi kalau aplikasi ditutup di tengah jalan
            string path = PathFor(key);
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string key) => Path.Combine(_folder, key + ".json");

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

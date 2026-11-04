using System.Text.Json;

namespace MusicPlayer.Library;

/// <summary>
/// Antrean pemutar yang disimpan saat aplikasi ditutup, supaya bisa dilanjutkan saat dibuka lagi (lihat
/// TrackQueue.Snapshot dan Restore). Lagu disimpan sebagai path; lagu yang sudah tidak ada di library dilewati saat
/// dipulihkan. Shuffle, repeat, dan Autoplay tidak ikut: pengaturannya disimpan terpisah.
/// </summary>
public sealed record PlaybackSession(string Current, TimeSpan Position, IReadOnlyList<string> Previous, IReadOnlyList<SessionEntry> UpNext);

/// <summary>Satu baris Playing Next di <see cref="PlaybackSession"/>.</summary>
/// <param name="OriginalIndex">
/// Urutan baris ini sebelum Playing Next diacak, untuk dikembalikan saat shuffle dimatikan; -1 kalau baris ini tidak
/// punya urutan asli (ditambahkan selama shuffle aktif, atau shuffle mati).
/// </param>
public sealed record SessionEntry(string Path, bool IsAutoplay, int OriginalIndex);

/// <summary>
/// Menyimpan dan membaca <see cref="PlaybackSession"/> sebagai file JSON. JSON ditulis dan dibaca manual (bukan
/// JsonSerializer), seperti LyricsCache, supaya tetap jalan saat aplikasi di-publish dengan trimming.
/// </summary>
public static class PlaybackSessionFile
{
    private const int Version = 1;

    /// <summary>Null kalau belum ada sesi, file-nya rusak, atau dari versi format lain.</summary>
    public static PlaybackSession? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            if (root.GetProperty("version").GetInt32() != Version)
                return null;

            var previous = root.GetProperty("previous").EnumerateArray().Select(e => e.GetString()!).ToList();
            var upNext = root.GetProperty("upNext").EnumerateArray()
                .Select(e => new SessionEntry(
                    e.GetProperty("path").GetString()!,
                    e.GetProperty("autoplay").GetBoolean(),
                    e.GetProperty("original").GetInt32()))
                .ToList();

            return new PlaybackSession(
                root.GetProperty("current").GetString()!,
                TimeSpan.FromMilliseconds(root.GetProperty("positionMs").GetInt64()),
                previous,
                upNext);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null; // file rusak atau tidak terbaca: aplikasi mulai tanpa sesi
        }
    }

    /// <summary>
    /// Disimpan sebisanya: kalau gagal (misalnya disk penuh), sesi sebelumnya tetap ada. Null menghapus sesi yang tersimpan
    /// (tidak ada lagu yang diputar).
    /// </summary>
    public static void Save(string path, PlaybackSession? session)
    {
        try
        {
            if (session is null)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Version);
                writer.WriteString("current", session.Current);
                writer.WriteNumber("positionMs", (long)session.Position.TotalMilliseconds);
                writer.WriteStartArray("previous");
                foreach (string previous in session.Previous)
                    writer.WriteStringValue(previous);
                writer.WriteEndArray();
                writer.WriteStartArray("upNext");
                foreach (var entry in session.UpNext)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", entry.Path);
                    writer.WriteBoolean("autoplay", entry.IsAutoplay);
                    writer.WriteNumber("original", entry.OriginalIndex);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            // Ditulis ke file sementara dulu, supaya tidak ada file setengah jadi kalau aplikasi ditutup di tengah jalan
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

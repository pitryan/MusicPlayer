using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Keterangan artis untuk halaman artis (About, asal, tanggal lahir atau terbentuk). Yang tersimpan langsung dipakai;
/// yang belum ada atau sudah lama diambil dari internet (ArtistInfoClient), lalu disimpan. Kalau internet gagal, yang
/// tersimpan tetap ditampilkan. Foto artis tidak diambil (lihat ArtistInfo): banner dan daftar artis memakai cover.
/// </summary>
public sealed class ArtistInfoService
{
    private readonly LibraryDatabase _database;
    private readonly ArtistInfoClient _client;

    public ArtistInfoService(LibraryDatabase database, ArtistInfoClient client)
    {
        _database = database;
        _client = client;
    }

    /// <summary>Yang tersimpan saja, tanpa internet: untuk menampilkan halaman seketika.</summary>
    public ArtistInfo? Cached(string name) => _database.GetArtistInfo(name);

    /// <summary>Keterangan terbaru: dari simpanan kalau masih segar, kalau tidak diambil dari internet.</summary>
    public async Task<ArtistInfo?> GetAsync(string name, CancellationToken ct)
    {
        var cached = _database.GetArtistInfo(name);
        if (name.Length == 0 || (cached is not null && cached.IsFresh(DateTime.UtcNow)))
            return cached;

        try
        {
            string? musicBrainzId = await Task.Run(() => MusicBrainzIdOf(name), ct);
            var fetched = await _client.FetchAsync(name, musicBrainzId, ct);
            _database.SaveArtistInfo(fetched);
            return fetched;
        }
        catch (Exception)
        {
            // Internet mati, layanan sedang sibuk, atau halaman sudah ditinggalkan: tetap pakai yang tersimpan
            return cached;
        }
    }

    /// <summary>
    /// ID artis MusicBrainz dari tag Picard di salah satu lagu artis ini: pencocokan paling tepat, tanpa menebak dari
    /// nama. Kalau namanya diambil dari Album Artist, yang dibaca ID album artist; kalau tidak, ID artis lagunya.
    /// </summary>
    private string? MusicBrainzIdOf(string name)
    {
        var track = _database.GetTracks().FirstOrDefault(t =>
            string.Equals(ArtistNames.Primary(t.AlbumArtist ?? t.Artist), name, StringComparison.OrdinalIgnoreCase));
        if (track is null)
            return null;

        try
        {
            var tags = TagEditor.Read(track.FilePath);
            string key = track.AlbumArtist is { Length: > 0 } ? "musicbrainz_albumartistid" : "musicbrainz_artistid";
            string value = tags.Get(key);
            if (value.Length == 0 && key == "musicbrainz_albumartistid")
                value = tags.Get("musicbrainz_artistid");
            // Lagu dengan beberapa artis menyimpan beberapa ID; yang pertama milik artis utamanya
            return TagFields.Split(value, multiValue: true).FirstOrDefault(id => id.Length > 0);
        }
        catch (Exception)
        {
            return null; // file dipindah atau tidak bisa dibaca: artis dicari lewat namanya
        }
    }
}

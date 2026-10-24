using Microsoft.Data.Sqlite;

namespace MusicPlayer.Library;

public enum AddFolderStatus { Added, AlreadyCovered, NotFound }

/// <summary>Hasil AddFolder. Untuk AlreadyCovered, Folder berisi folder induk yang sudah ada di library.</summary>
public sealed record AddFolderResult(AddFolderStatus Status, string Folder);

public sealed record LibraryStats(
    int Tracks, TimeSpan TotalDuration, int Flac, int Mp3, int HiRes,
    int Albums, int Artists, int WithCover, int WithLyrics);

/// <summary>
/// Database library berbasis SQLite: daftar folder sumber dan data semua lagu di dalamnya.
/// </summary>
public sealed class LibraryDatabase
{
    private const string TrackColumns =
        "id, path, folder, file_size, modified_utc, title, artist, album, album_artist, genre, year, " +
        "track_number, track_count, disc_number, disc_count, duration_ms, codec, sample_rate, " +
        "bits_per_sample, channels, bitrate_kbps, has_cover, has_lyrics, release_date, copyright, release_type";

    // Disc kosong dianggap disc 1, dan lagu tanpa nomor track ditaruh di akhir albumnya
    // Kolom yang sama dengan TrackColumns, tapi diawali "t." untuk query yang menggabungkan dua tabel
    private static readonly string TrackColumnsT =
        string.Join(", ", TrackColumns.Split(',').Select(c => "t." + c.Trim()));

    private const string AlbumOrder =
        "ORDER BY coalesce(album_artist, artist) COLLATE NOCASE, album COLLATE NOCASE, " +
        "coalesce(disc_number, 1), coalesce(track_number, 999999), path";

    // Membandingkan awal path dengan folder, termasuk semua subfolder di dalamnya
    private const string UnderFolder = "substr(path, 1, length($folder)) = $folder COLLATE NOCASE";

    private readonly string _connectionString;

    public LibraryDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    // ── Folder library ───────────────────────────────────────

    public IReadOnlyList<string> GetFolders()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_folders ORDER BY path";
        using var reader = command.ExecuteReader();

        var folders = new List<string>();
        while (reader.Read())
            folders.Add(reader.GetString(0));
        return folders;
    }

    public AddFolderResult AddFolder(string folderPath)
    {
        string folder = LibraryPaths.NormalizeFolder(folderPath);
        if (!Directory.Exists(folder))
            return new AddFolderResult(AddFolderStatus.NotFound, folder);

        var existing = GetFolders();
        string? parent = existing.FirstOrDefault(f => LibraryPaths.IsUnder(folder, f));
        if (parent is not null)
            return new AddFolderResult(AddFolderStatus.AlreadyCovered, parent);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // Folder lama yang berada di dalam folder baru tidak perlu disimpan terpisah lagi
        foreach (string child in existing.Where(f => LibraryPaths.IsUnder(f, folder)))
            Execute(connection, "DELETE FROM library_folders WHERE path = $path", ("$path", child));

        Execute(connection, "INSERT INTO library_folders (path) VALUES ($path)", ("$path", folder));
        transaction.Commit();
        return new AddFolderResult(AddFolderStatus.Added, folder);
    }

    /// <summary>
    /// Menghapus folder dari library beserta daftar lagunya (file aslinya tidak disentuh).
    /// Mengembalikan jumlah lagu yang dihapus dari database.
    /// </summary>
    public int RemoveFolder(string folderPath)
    {
        string folder = LibraryPaths.NormalizeFolder(folderPath);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM library_folders WHERE path = $path", ("$path", folder));

        // Hapus semua lagu yang tidak lagi berada di bawah folder library mana pun
        int removed = Execute(connection, """
            DELETE FROM tracks
            WHERE NOT EXISTS (
                SELECT 1 FROM library_folders f
                WHERE substr(tracks.path, 1, length(f.path)) = f.path COLLATE NOCASE)
            """);

        transaction.Commit();
        return removed;
    }

    // ── Dipakai oleh LibraryScanner ──────────────────────────

    public Dictionary<string, FileStamp> GetFileStamps()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, file_size, modified_utc FROM tracks";
        using var reader = command.ExecuteReader();

        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            stamps[reader.GetString(0)] = new FileStamp(reader.GetInt64(1), reader.GetInt64(2));
        return stamps;
    }

    /// <summary>Menambah lagu baru atau memperbarui lagu yang sudah ada (berdasarkan path).</summary>
    public void UpsertTracks(IEnumerable<Track> tracks)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tracks (
                path, folder, file_size, modified_utc, title, artist, album, album_artist, genre, year,
                track_number, track_count, disc_number, disc_count, duration_ms, codec, sample_rate,
                bits_per_sample, channels, bitrate_kbps, has_cover, has_lyrics, added_utc, release_date, copyright, release_type)
            VALUES (
                $path, $folder, $file_size, $modified_utc, $title, $artist, $album, $album_artist, $genre, $year,
                $track_number, $track_count, $disc_number, $disc_count, $duration_ms, $codec, $sample_rate,
                $bits_per_sample, $channels, $bitrate_kbps, $has_cover, $has_lyrics, $added_utc, $release_date, $copyright, $release_type)
            ON CONFLICT(path) DO UPDATE SET
                folder = excluded.folder, file_size = excluded.file_size, modified_utc = excluded.modified_utc,
                title = excluded.title, artist = excluded.artist, album = excluded.album,
                album_artist = excluded.album_artist, genre = excluded.genre, year = excluded.year,
                track_number = excluded.track_number, track_count = excluded.track_count,
                disc_number = excluded.disc_number, disc_count = excluded.disc_count,
                duration_ms = excluded.duration_ms, codec = excluded.codec, sample_rate = excluded.sample_rate,
                bits_per_sample = excluded.bits_per_sample, channels = excluded.channels,
                bitrate_kbps = excluded.bitrate_kbps, has_cover = excluded.has_cover,
                has_lyrics = excluded.has_lyrics, release_date = excluded.release_date, copyright = excluded.copyright,
                release_type = excluded.release_type
            """;

        // Parameter dibuat sekali lalu diisi ulang untuk tiap lagu: jauh lebih cepat untuk ribuan baris
        string[] names =
        [
            "$path", "$folder", "$file_size", "$modified_utc", "$title", "$artist", "$album", "$album_artist",
            "$genre", "$year", "$track_number", "$track_count", "$disc_number", "$disc_count", "$duration_ms",
            "$codec", "$sample_rate", "$bits_per_sample", "$channels", "$bitrate_kbps", "$has_cover",
            "$has_lyrics", "$added_utc", "$release_date", "$copyright", "$release_type",
        ];
        foreach (string name in names)
            command.Parameters.AddWithValue(name, DBNull.Value);

        foreach (var t in tracks)
        {
            Set(command, "$path", t.FilePath);
            Set(command, "$folder", t.FolderPath);
            Set(command, "$file_size", t.FileSize);
            Set(command, "$modified_utc", t.ModifiedUtcTicks);
            Set(command, "$title", t.Title);
            Set(command, "$artist", t.Artist);
            Set(command, "$album", t.Album);
            Set(command, "$album_artist", t.AlbumArtist);
            Set(command, "$genre", t.Genre);
            Set(command, "$year", t.Year);
            Set(command, "$track_number", t.TrackNumber);
            Set(command, "$track_count", t.TrackCount);
            Set(command, "$disc_number", t.DiscNumber);
            Set(command, "$disc_count", t.DiscCount);
            Set(command, "$release_date", t.ReleaseDate);
            Set(command, "$copyright", t.Copyright);
            Set(command, "$release_type", t.ReleaseType);
            Set(command, "$duration_ms", (long)t.Duration.TotalMilliseconds);
            Set(command, "$codec", t.Codec);
            Set(command, "$sample_rate", t.SampleRate);
            Set(command, "$bits_per_sample", t.BitsPerSample);
            Set(command, "$channels", t.Channels);
            Set(command, "$bitrate_kbps", t.BitrateKbps);
            Set(command, "$has_cover", t.HasCover);
            Set(command, "$has_lyrics", t.HasLyrics);
            // "Tanggal ditambahkan" = waktu ubah file saat pertama kali masuk library. Kolom ini tidak
            // ikut diperbarui saat scan ulang, jadi mengedit tag nanti tidak membuat lagu terlihat "baru".
            Set(command, "$added_utc", t.ModifiedUtcTicks);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void DeleteTracks(IEnumerable<string> paths)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tracks WHERE path = $path";
        var parameter = command.Parameters.AddWithValue("$path", "");

        foreach (string path in paths)
        {
            parameter.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // ── Query untuk tampilan ─────────────────────────────────

    /// <summary>
    /// Semua album, diurutkan per artis lalu tahun. Lagu tanpa nama album tidak ikut.
    /// Kalau genre diisi, hanya lagu bergenre itu yang dihitung.
    /// </summary>
    public IReadOnlyList<AlbumSummary> GetAlbums(string? genre = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        string genreFilter = genre is null ? "" : "AND genre = $genre COLLATE NOCASE";
        if (genre is not null)
            command.Parameters.AddWithValue("$genre", genre);
        // Judul, artis, dan cover album diambil dari lagu pertamanya (disc dan nomor track terkecil).
        // "rn = 1" menandai lagu pertama itu di setiap album.
        command.CommandText = $"""
            WITH ranked AS (
                SELECT *,
                       coalesce(album_artist, artist, '') AS album_key,
                       row_number() OVER (
                           PARTITION BY coalesce(album_artist, artist, '') COLLATE NOCASE, album COLLATE NOCASE
                           ORDER BY coalesce(disc_number, 1), coalesce(track_number, 999999), path
                       ) AS rn
                FROM tracks
                WHERE album IS NOT NULL {genreFilter}
            )
            SELECT max(CASE WHEN rn = 1 THEN album END),
                   max(CASE WHEN rn = 1 THEN album_key END),
                   max(year),
                   count(*),
                   sum(duration_ms),
                   max(genre),
                   max(CASE WHEN rn = 1 THEN path END),
                   max(added_utc),
                   max(release_type),
                   max(track_count),
                   max(duration_ms),
                   max(release_date)
            FROM ranked
            GROUP BY album_key COLLATE NOCASE, album COLLATE NOCASE
            ORDER BY album_key COLLATE NOCASE, max(year), album COLLATE NOCASE
            """;
        using var r = command.ExecuteReader();

        var albums = new List<AlbumSummary>();
        while (r.Read())
        {
            albums.Add(new AlbumSummary(
                Title: r.GetString(0),
                Artist: r.GetString(1),
                Year: Number(r, 2),
                TrackCount: r.GetInt32(3),
                Duration: TimeSpan.FromMilliseconds(r.GetInt64(4)),
                Genre: Text(r, 5),
                CoverTrackPath: r.GetString(6),
                AddedUtc: new DateTime(r.GetInt64(7), DateTimeKind.Utc))
            {
                ReleaseTypeTag = Text(r, 8),
                TrackTotal = Number(r, 9),
                LongestTrack = TimeSpan.FromMilliseconds(r.GetInt64(10)),
                ReleaseDate = Text(r, 11),
            });
        }
        return albums;
    }

    /// <summary>
    /// Semua artis, diambil dari artis album (tanpa bagian "feat."), urut nama.
    /// Album yang artisnya sama setelah "feat." dibuang akan tergabung di satu artis.
    /// </summary>
    public IReadOnlyList<ArtistSummary> GetArtists() =>
        GetAlbums()
            .GroupBy(a => ArtistNames.Primary(a.Artist), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArtistSummary(
                Name: g.Key,
                AlbumCount: g.Count(),
                TrackCount: g.Sum(a => a.TrackCount),
                CoverTrackPath: g.OrderByDescending(a => a.Year ?? 0).First().CoverTrackPath))
            .OrderBy(a => a.Name.Length == 0) // "Unknown Artist" di paling bawah
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Album-album milik satu artis (lihat <see cref="GetArtists"/>), yang terbaru lebih dulu.</summary>
    public IReadOnlyList<AlbumSummary> GetArtistAlbums(string artistName) =>
        GetAlbums()
            .Where(a => string.Equals(ArtistNames.Primary(a.Artist), artistName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Year ?? 0)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Isi halaman artis: rilis terbaru, lagu paling sering diputar (dari History), album, single dan EP, serta album
    /// artis lain tempat artis ini ikut tercantum (Appears On). Semua daftar rilis urut dari yang terbaru.
    /// </summary>
    public ArtistOverview GetArtistOverview(string artistName, int topSongLimit = 10)
    {
        bool IsOwn(string? albumArtist) =>
            string.Equals(ArtistNames.Primary(albumArtist), artistName, StringComparison.OrdinalIgnoreCase);

        var albums = GetAlbums();
        var own = albums
            .Where(a => IsOwn(a.Artist))
            .OrderByDescending(a => a.ReleaseSortKey, StringComparer.Ordinal)
            .ThenByDescending(a => a.AddedUtc)
            .ToList();

        // Appears On: lagu yang mencantumkan artis ini di album milik artis lain. LIKE hanya menyaring kasar supaya
        // tidak semua lagu dibaca; pencocokan sebenarnya oleh ArtistNames.IsCredited.
        var guestAlbums = new HashSet<(string, string)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {TrackColumns} FROM tracks
                WHERE album IS NOT NULL AND (artist LIKE $pattern ESCAPE '\' OR title LIKE $pattern ESCAPE '\')
                """;
            command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(artistName)}%");
            foreach (var track in ReadTracks(command))
            {
                if (!IsOwn(track.AlbumArtist ?? track.Artist) && ArtistNames.IsCredited(track, artistName))
                    guestAlbums.Add(((track.AlbumArtist ?? track.Artist ?? "").ToUpperInvariant(), track.Album!.ToUpperInvariant()));
            }
        }

        var appearsOn = albums
            .Where(a => !IsOwn(a.Artist) && guestAlbums.Contains((a.Artist.ToUpperInvariant(), a.Title.ToUpperInvariant())))
            .OrderByDescending(a => a.ReleaseSortKey, StringComparer.Ordinal)
            .ToList();

        var topSongs = GetMostPlayed(int.MaxValue)
            .Where(c => IsOwn(c.Track.AlbumArtist ?? c.Track.Artist) || ArtistNames.IsCredited(c.Track, artistName))
            .Take(topSongLimit)
            .ToList();

        return new ArtistOverview(
            artistName,
            own.FirstOrDefault(),
            topSongs,
            own.Where(a => a.Kind == ReleaseKind.Album).ToList(),
            own.Where(a => a.Kind != ReleaseKind.Album).ToList(),
            appearsOn);
    }

    /// <summary>Semua genre beserta jumlah album dan lagunya, urut nama. Lagu tanpa genre tidak ikut.</summary>
    public IReadOnlyList<GenreSummary> GetGenres()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT min(genre), -- dari variasi huruf besar/kecil, pilih yang berawalan huruf besar ("J-Pop", bukan "j-pop")
                   count(DISTINCT CASE WHEN album IS NOT NULL
                       THEN lower(coalesce(album_artist, artist, '')) || '|' || lower(album) END),
                   count(*),
                   min(path)
            FROM tracks
            WHERE genre IS NOT NULL
            GROUP BY genre COLLATE NOCASE
            ORDER BY genre COLLATE NOCASE
            """;
        using var r = command.ExecuteReader();

        var genres = new List<GenreSummary>();
        while (r.Read())
            genres.Add(new GenreSummary(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
        return genres;
    }

    /// <summary>Subfolder langsung dari sebuah folder (yang berisi lagu), urut nama.</summary>
    public IReadOnlyList<FolderSummary> GetSubfolders(string folder)
    {
        string prefix = LibraryPaths.NormalizeFolder(folder);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT path FROM tracks WHERE {UnderFolder} ORDER BY path COLLATE NOCASE";
        command.Parameters.AddWithValue("$folder", prefix);
        using var r = command.ExecuteReader();

        // Dari setiap path lagu, ambil nama folder pertama setelah folder yang sedang dibuka
        var children = new Dictionary<string, (int Count, string FirstPath)>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            string path = r.GetString(0);
            string relative = path[prefix.Length..];
            int separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (separator < 0)
                continue; // file langsung di folder ini, bukan di subfolder

            string name = relative[..separator];
            children[name] = children.TryGetValue(name, out var info)
                ? (info.Count + 1, info.FirstPath)
                : (1, path);
        }

        return children
            .Select(c => new FolderSummary(Path.Combine(prefix, c.Key), c.Key, c.Value.Count, c.Value.FirstPath))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Lagu di sebuah folder, urut path. Bisa hanya file langsung di folder itu, atau termasuk semua subfolder.</summary>
    public IReadOnlyList<Track> GetFolderTracks(string folder, bool includeSubfolders)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (includeSubfolders)
        {
            command.CommandText = $"SELECT {TrackColumns} FROM tracks WHERE {UnderFolder} ORDER BY path COLLATE NOCASE";
            command.Parameters.AddWithValue("$folder", LibraryPaths.NormalizeFolder(folder));
        }
        else
        {
            command.CommandText = $"SELECT {TrackColumns} FROM tracks WHERE folder = $dir COLLATE NOCASE ORDER BY path COLLATE NOCASE";
            command.Parameters.AddWithValue("$dir", LibraryPaths.WithoutTrailingSeparator(folder));
        }
        return ReadTracks(command);
    }

    /// <summary>Album tempat sebuah lagu berada (untuk menu "Go to Album"), atau null kalau lagunya tanpa album.</summary>
    public AlbumSummary? FindAlbum(Track track)
    {
        if (track.Album is null)
            return null;

        // Kunci album sama seperti di GetAlbums: album artist, atau artis lagu kalau album artist kosong
        string artist = track.AlbumArtist ?? track.Artist ?? "";
        return GetAlbums().FirstOrDefault(a =>
            string.Equals(a.Title, track.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lagu-lagu sebuah album, urut disc lalu nomor track.</summary>
    public IReadOnlyList<Track> GetAlbumTracks(string artist, string album)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumns} FROM tracks
            WHERE album = $album COLLATE NOCASE
              AND coalesce(album_artist, artist, '') = $artist COLLATE NOCASE
            {AlbumOrder}
            """;
        command.Parameters.AddWithValue("$album", album);
        command.Parameters.AddWithValue("$artist", artist);
        return ReadTracks(command);
    }

    /// <summary>Lagu di dalam sebuah folder, termasuk subfolder-nya. Folder null berarti seluruh library.</summary>
    public IReadOnlyList<Track> GetTracks(string? folder = null, int limit = int.MaxValue)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        string where = folder is null ? "" : $"WHERE {UnderFolder}";
        command.CommandText = $"SELECT {TrackColumns} FROM tracks {where} {AlbumOrder} LIMIT $limit";
        if (folder is not null)
            command.Parameters.AddWithValue("$folder", LibraryPaths.NormalizeFolder(folder));
        command.Parameters.AddWithValue("$limit", limit);
        return ReadTracks(command);
    }

    public int CountTracks(string? folder = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = folder is null
            ? "SELECT count(*) FROM tracks"
            : $"SELECT count(*) FROM tracks WHERE {UnderFolder}";
        if (folder is not null)
            command.Parameters.AddWithValue("$folder", LibraryPaths.NormalizeFolder(folder));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Mencari di judul, artis, album, dan album artist.</summary>
    public IReadOnlyList<Track> Search(string text, int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumns} FROM tracks
            WHERE title LIKE $q ESCAPE '\' OR artist LIKE $q ESCAPE '\'
               OR album LIKE $q ESCAPE '\' OR album_artist LIKE $q ESCAPE '\'
            {AlbumOrder} LIMIT $limit
            """;
        command.Parameters.AddWithValue("$q", "%" + EscapeLike(text.Trim()) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        return ReadTracks(command);
    }

    public LibraryStats GetStats()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*),
                   coalesce(sum(duration_ms), 0),
                   count(*) FILTER (WHERE codec = 'FLAC'),
                   count(*) FILTER (WHERE codec = 'MP3'),
                   count(*) FILTER (WHERE bits_per_sample > 16 OR sample_rate > 48000),
                   count(DISTINCT coalesce(album_artist, artist, '') || '|' || album),
                   count(DISTINCT artist),
                   count(*) FILTER (WHERE has_cover),
                   count(*) FILTER (WHERE has_lyrics)
            FROM tracks
            """;
        using var r = command.ExecuteReader();
        r.Read();
        return new LibraryStats(
            Tracks: r.GetInt32(0),
            TotalDuration: TimeSpan.FromMilliseconds(r.GetInt64(1)),
            Flac: r.GetInt32(2),
            Mp3: r.GetInt32(3),
            HiRes: r.GetInt32(4),
            Albums: r.GetInt32(5),
            Artists: r.GetInt32(6),
            WithCover: r.GetInt32(7),
            WithLyrics: r.GetInt32(8));
    }

    // ── Riwayat putar ────────────────────────────────────────

    public void RecordPlay(string path, DateTime playedUtc)
    {
        using var connection = Open();
        Execute(connection, "INSERT INTO plays (path, played_utc) VALUES ($path, $time)",
            ("$path", path), ("$time", playedUtc.Ticks));
    }

    /// <summary>Lagu yang terakhir diputar, yang terbaru lebih dulu. Lagu yang sudah tidak ada di library dilewati.</summary>
    public IReadOnlyList<PlayRecord> GetRecentPlays(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumnsT}, p.played_utc
            FROM plays p JOIN tracks t ON t.path = p.path
            ORDER BY p.played_utc DESC, p.id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var r = command.ExecuteReader();

        var plays = new List<PlayRecord>();
        while (r.Read())
            plays.Add(new PlayRecord(ReadTrack(r), new DateTime(r.GetInt64(TrackColumnCount), DateTimeKind.Utc)));
        return plays;
    }

    /// <summary>
    /// Lagu yang paling sering diputar. Kalau jumlahnya sama, yang terakhir diputar lebih dulu. Dengan
    /// <paramref name="sinceUtc"/>, hanya putaran sejak waktu itu yang dihitung (misalnya 30 hari terakhir).
    /// </summary>
    public IReadOnlyList<PlayCount> GetMostPlayed(int limit = 100, DateTime? sinceUtc = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumnsT}, count(*) AS plays, max(p.played_utc) AS last_played
            FROM plays p JOIN tracks t ON t.path = p.path
            WHERE p.played_utc >= $since
            GROUP BY t.id
            ORDER BY plays DESC, last_played DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$since", sinceUtc?.Ticks ?? 0L);
        using var r = command.ExecuteReader();

        var result = new List<PlayCount>();
        while (r.Read())
        {
            result.Add(new PlayCount(
                ReadTrack(r),
                r.GetInt32(TrackColumnCount),
                new DateTime(r.GetInt64(TrackColumnCount + 1), DateTimeKind.Utc)));
        }
        return result;
    }

    /// <summary>
    /// Album yang terakhir diputar (lagu mana pun di dalamnya), yang terbaru lebih dulu. Kunci albumnya sama dengan
    /// GetAlbums, jadi kotak dan halaman albumnya juga sama.
    /// </summary>
    public IReadOnlyList<RecentAlbum> GetRecentlyPlayedAlbums(int limit = 60)
    {
        var recent = new List<(string Artist, string Title, DateTime LastPlayed)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT coalesce(t.album_artist, t.artist, '') AS album_key, t.album, max(p.played_utc) AS last_played
                FROM plays p JOIN tracks t ON t.path = p.path
                WHERE t.album IS NOT NULL
                GROUP BY album_key COLLATE NOCASE, t.album COLLATE NOCASE
                ORDER BY last_played DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var r = command.ExecuteReader();
            while (r.Read())
                recent.Add((r.GetString(0), r.GetString(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc)));
        }

        // TryAdd, bukan ToDictionary: NOCASE di SQLite hanya melipat huruf ASCII, jadi dua album yang hanya beda huruf
        // besar/kecil non-ASCII bisa tetap terpisah di sana
        var albums = new Dictionary<(string, string), AlbumSummary>();
        foreach (var album in GetAlbums())
            albums.TryAdd((album.Artist.ToUpperInvariant(), album.Title.ToUpperInvariant()), album);

        return recent
            .Where(x => albums.ContainsKey((x.Artist.ToUpperInvariant(), x.Title.ToUpperInvariant())))
            .Select(x => new RecentAlbum(albums[(x.Artist.ToUpperInvariant(), x.Title.ToUpperInvariant())], x.LastPlayed))
            .ToList();
    }

    // ── Kualitas sumber ─────────────────────────────────────

    private const string QualityColumns =
        "q.path, q.sample_rate, q.container_bits, q.is_float, q.effective_bits, q.cutoff_hz, q.verdict, q.source_rate";

    /// <summary>Menyimpan (atau mengganti) hasil pemeriksaan satu lagu, dengan stempel file saat diperiksa.</summary>
    public void SaveQualityReport(string path, FileStamp stamp, QualityReport report, DateTime nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO quality_reports (path, file_size, modified_utc, analyzer_version, sample_rate, container_bits,
                is_float, effective_bits, cutoff_hz, verdict, source_rate, spectrum, analyzed_utc)
            VALUES ($path, $size, $modified, $version, $rate, $bits, $float, $effective, $cutoff, $verdict, $source, $spectrum, $now)
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", stamp.Size);
        command.Parameters.AddWithValue("$modified", stamp.ModifiedUtcTicks);
        command.Parameters.AddWithValue("$version", QualityAnalyzer.Version);
        command.Parameters.AddWithValue("$rate", report.SampleRate);
        command.Parameters.AddWithValue("$bits", report.ContainerBits);
        command.Parameters.AddWithValue("$float", report.IsFloat ? 1 : 0);
        command.Parameters.AddWithValue("$effective", (object?)report.EffectiveBits ?? DBNull.Value);
        command.Parameters.AddWithValue("$cutoff", (object?)report.CutoffHz ?? DBNull.Value);
        command.Parameters.AddWithValue("$verdict", (int)report.Verdict);
        command.Parameters.AddWithValue("$source", (object?)report.LikelySourceRate ?? DBNull.Value);
        command.Parameters.AddWithValue("$spectrum", EncodeSpectrum(report.Spectrum));
        command.Parameters.AddWithValue("$now", nowUtc.Ticks);
        command.ExecuteNonQuery();
    }

    /// <summary>Hasil lengkap (dengan spektrum) satu lagu, atau null kalau belum diperiksa atau sudah tidak berlaku.</summary>
    public QualityReport? GetQualityReport(string path, FileStamp stamp)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QualityColumns}, q.spectrum FROM quality_reports q
            WHERE q.path = $path AND q.file_size = $size AND q.modified_utc = $modified AND q.analyzer_version = $version
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", stamp.Size);
        command.Parameters.AddWithValue("$modified", stamp.ModifiedUtcTicks);
        command.Parameters.AddWithValue("$version", QualityAnalyzer.Version);
        using var r = command.ExecuteReader();
        return r.Read() ? ReadQuality(r, withSpectrum: true) : null;
    }

    /// <summary>
    /// Semua hasil yang masih berlaku untuk lagu di library (stempel file sama dengan isi database lagunya), per path.
    /// Tanpa spektrum, supaya ringan untuk ribuan lagu; spektrum satu lagu diambil lewat GetQualityReport.
    /// </summary>
    public IReadOnlyDictionary<string, QualityReport> GetQualityReports()
    {
        var reports = new Dictionary<string, QualityReport>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {QualityColumns} FROM quality_reports q
            JOIN tracks t ON t.path = q.path AND t.file_size = q.file_size AND t.modified_utc = q.modified_utc
            WHERE q.analyzer_version = $version
            """;
        command.Parameters.AddWithValue("$version", QualityAnalyzer.Version);
        using var r = command.ExecuteReader();
        while (r.Read())
            reports[r.GetString(0)] = ReadQuality(r, withSpectrum: false);
        return reports;
    }

    private static QualityReport ReadQuality(SqliteDataReader r, bool withSpectrum)
    {
        int sampleRate = r.GetInt32(1);
        return new QualityReport(
            sampleRate,
            r.GetInt32(2),
            r.GetInt32(3) != 0,
            r.IsDBNull(4) ? null : r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetDouble(5),
            (SourceVerdict)r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetInt32(7),
            withSpectrum && !r.IsDBNull(8) ? DecodeSpectrum((byte[])r.GetValue(8), sampleRate) : []);
    }

    // Spektrum disimpan ringkas: level setiap titik dalam persepuluhan dB sebagai Int16 (300 titik = 600 byte).
    // Frekuensi titik ke-i dihitung ulang dari sample rate, karena titik-titiknya tersebar rata dari 0 sampai batas format.
    private static byte[] EncodeSpectrum(IReadOnlyList<SpectrumPoint> spectrum)
    {
        var bytes = new byte[spectrum.Count * 2];
        for (int i = 0; i < spectrum.Count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2),
                (short)Math.Clamp(Math.Round(spectrum[i].Db * 10), short.MinValue, short.MaxValue));
        return bytes;
    }

    private static List<SpectrumPoint> DecodeSpectrum(byte[] bytes, int sampleRate)
    {
        int count = bytes.Length / 2;
        double nyquist = sampleRate / 2.0;
        var points = new List<SpectrumPoint>(count);
        for (int i = 0; i < count; i++)
            points.Add(new SpectrumPoint((i + 0.5) * nyquist / count,
                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) / 10.0));
        return points;
    }

    // ── Favorit ──────────────────────────────────────────────

    /// <summary>Path semua lagu favorit, termasuk yang filenya sedang tidak ada di library.</summary>
    public IReadOnlyList<string> GetFavouritePaths()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM favourites";
        using var reader = command.ExecuteReader();

        var paths = new List<string>();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    /// <summary>Lagu favorit yang ada di library, yang terakhir difavoritkan lebih dulu.</summary>
    public IReadOnlyList<Track> GetFavouriteTracks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumnsT}
            FROM favourites f JOIN tracks t ON t.path = f.path
            ORDER BY f.added_utc DESC, t.path
            """;
        return ReadTracks(command);
    }

    /// <summary>
    /// Menandai atau melepas favorit. Menandai lagu yang sudah favorit tidak mengubah waktunya, jadi urutan di
    /// Favourite Songs tetap.
    /// </summary>
    public void SetFavourite(string path, bool favourite, DateTime nowUtc)
    {
        using var connection = Open();
        if (favourite)
        {
            Execute(connection, "INSERT INTO favourites (path, added_utc) VALUES ($path, $time) ON CONFLICT(path) DO NOTHING",
                ("$path", path), ("$time", nowUtc.Ticks));
        }
        else
        {
            Execute(connection, "DELETE FROM favourites WHERE path = $path", ("$path", path));
        }
    }

    // ── Playlist ─────────────────────────────────────────────

    /// <summary>
    /// Semua playlist, urut nama. Jumlah lagu, durasi, dan cover hanya menghitung lagu yang ada di library; isi yang
    /// filenya sedang tidak ada tetap tersimpan dan kembali kalau filenya kembali.
    /// </summary>
    public IReadOnlyList<PlaylistSummary> GetPlaylists()
    {
        using var connection = Open();
        var playlists = new List<(long Id, string Name, DateTime Modified)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, modified_utc FROM playlists ORDER BY name COLLATE NOCASE, id";
            using var r = command.ExecuteReader();
            while (r.Read())
                playlists.Add((r.GetInt64(0), r.GetString(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc)));
        }

        var contents = playlists.ToDictionary(p => p.Id, _ => (Count: 0, DurationMs: 0L, Albums: new List<string>(), Covers: new List<string>()));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.playlist_id, t.path, coalesce(t.album_artist, t.artist, '') || '|' || coalesce(t.album, ''), t.duration_ms
                FROM playlist_items i JOIN tracks t ON t.path = i.path
                ORDER BY i.playlist_id, i.position, i.id
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                if (!contents.TryGetValue(r.GetInt64(0), out var c))
                    continue;
                c.Count++;
                c.DurationMs += r.GetInt64(3);
                // Cover playlist diambil dari album yang berbeda, paling banyak 4, seperti mozaik Apple Music
                string album = r.GetString(2);
                if (c.Covers.Count < 4 && !c.Albums.Contains(album, StringComparer.OrdinalIgnoreCase))
                {
                    c.Albums.Add(album);
                    c.Covers.Add(r.GetString(1));
                }
                contents[r.GetInt64(0)] = c;
            }
        }

        return playlists.Select(p => new PlaylistSummary(
            p.Id, p.Name, contents[p.Id].Count, TimeSpan.FromMilliseconds(contents[p.Id].DurationMs),
            contents[p.Id].Covers, p.Modified)).ToList();
    }

    public long CreatePlaylist(string name, DateTime nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playlists (name, created_utc, modified_utc) VALUES ($name, $time, $time) RETURNING id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$time", nowUtc.Ticks);
        return (long)command.ExecuteScalar()!;
    }

    public void RenamePlaylist(long id, string name, DateTime nowUtc)
    {
        using var connection = Open();
        Execute(connection, "UPDATE playlists SET name = $name, modified_utc = $time WHERE id = $id",
            ("$name", name), ("$time", nowUtc.Ticks), ("$id", id));
    }

    /// <summary>Menghapus playlist beserta isinya. Lagunya sendiri tetap ada di library.</summary>
    public void DeletePlaylist(long id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM playlist_items WHERE playlist_id = $id", ("$id", id));
        Execute(connection, "DELETE FROM playlists WHERE id = $id", ("$id", id));
        transaction.Commit();
    }

    /// <summary>Isi playlist yang ada di library, sesuai urutannya.</summary>
    public IReadOnlyList<PlaylistEntry> GetPlaylistEntries(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackColumnsT}, i.id
            FROM playlist_items i JOIN tracks t ON t.path = i.path
            WHERE i.playlist_id = $id
            ORDER BY i.position, i.id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var r = command.ExecuteReader();

        var entries = new List<PlaylistEntry>();
        while (r.Read())
            entries.Add(new PlaylistEntry(r.GetInt64(TrackColumnCount), ReadTrack(r)));
        return entries;
    }

    /// <summary>Path semua isi playlist, termasuk yang filenya sedang tidak ada. Dipakai untuk mengecek lagu ganda.</summary>
    public IReadOnlyList<string> GetPlaylistPaths(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM playlist_items WHERE playlist_id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();

        var paths = new List<string>();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    /// <summary>Menambahkan lagu ke akhir playlist, sesuai urutan yang diberikan.</summary>
    public void AddToPlaylist(long id, IReadOnlyList<string> paths, DateTime nowUtc)
    {
        if (paths.Count == 0)
            return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var maxCommand = connection.CreateCommand();
        maxCommand.CommandText = "SELECT coalesce(max(position), -1) FROM playlist_items WHERE playlist_id = $id";
        maxCommand.Parameters.AddWithValue("$id", id);
        long position = (long)maxCommand.ExecuteScalar()!;

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO playlist_items (playlist_id, position, path, added_utc) VALUES ($id, $position, $path, $time)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.Add("$position", SqliteType.Integer);
        insert.Parameters.Add("$path", SqliteType.Text);
        insert.Parameters.AddWithValue("$time", nowUtc.Ticks);
        foreach (string path in paths)
        {
            Set(insert, "$position", ++position);
            Set(insert, "$path", path);
            insert.ExecuteNonQuery();
        }

        Touch(connection, id, nowUtc);
        transaction.Commit();
    }

    public void RemoveFromPlaylist(long id, IReadOnlyCollection<long> itemIds, DateTime nowUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (long itemId in itemIds)
            Execute(connection, "DELETE FROM playlist_items WHERE id = $item AND playlist_id = $id", ("$item", itemId), ("$id", id));
        Touch(connection, id, nowUtc);
        transaction.Commit();
    }

    /// <summary>
    /// Menyimpan urutan baru setelah lagu diseret. <paramref name="itemIdsInOrder"/> berisi semua isi yang tampil
    /// (GetPlaylistEntries) dalam urutan baru. Isi yang filenya sedang tidak ada tetap di tempatnya: posisi-posisi yang
    /// dipakai isi yang tampil dibagi ulang sesuai urutan baru.
    /// </summary>
    public void ReorderPlaylist(long id, IReadOnlyList<long> itemIdsInOrder, DateTime nowUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var visible = new List<(long ItemId, long Position)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, i.position FROM playlist_items i JOIN tracks t ON t.path = i.path
                WHERE i.playlist_id = $id ORDER BY i.position, i.id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var r = command.ExecuteReader();
            while (r.Read())
                visible.Add((r.GetInt64(0), r.GetInt64(1)));
        }

        // Daftar dari halaman harus berisi tepat isi yang tampil; kalau tidak (misalnya isinya berubah di tempat lain),
        // urutan lama dipertahankan daripada mengacak playlist
        if (itemIdsInOrder.Count != visible.Count || !itemIdsInOrder.OrderBy(x => x).SequenceEqual(visible.Select(v => v.ItemId).OrderBy(x => x)))
            throw new ArgumentException("The new order must contain exactly the visible playlist items.", nameof(itemIdsInOrder));

        // Posisi dalam satu playlist selalu berbeda (AddToPlaylist selalu menambah dari posisi terbesar), jadi
        // posisi-posisi itu cukup dibagikan ulang sesuai urutan baru
        var slots = visible.Select(v => v.Position).ToList();

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE playlist_items SET position = $position WHERE id = $item";
        update.Parameters.Add("$position", SqliteType.Integer);
        update.Parameters.Add("$item", SqliteType.Integer);
        for (int i = 0; i < itemIdsInOrder.Count; i++)
        {
            Set(update, "$position", slots[i]);
            Set(update, "$item", itemIdsInOrder[i]);
            update.ExecuteNonQuery();
        }

        Touch(connection, id, nowUtc);
        transaction.Commit();
    }

    private static void Touch(SqliteConnection connection, long playlistId, DateTime nowUtc) =>
        Execute(connection, "UPDATE playlists SET modified_utc = $time WHERE id = $id", ("$time", nowUtc.Ticks), ("$id", playlistId));

    // ── Bagian internal ──────────────────────────────────────

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS library_folders (
                path TEXT PRIMARY KEY COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS tracks (
                id              INTEGER PRIMARY KEY,
                path            TEXT NOT NULL UNIQUE COLLATE NOCASE,
                folder          TEXT NOT NULL COLLATE NOCASE,
                file_size       INTEGER NOT NULL,
                modified_utc    INTEGER NOT NULL,
                title           TEXT,
                artist          TEXT,
                album           TEXT,
                album_artist    TEXT,
                genre           TEXT,
                year            INTEGER,
                track_number    INTEGER,
                track_count     INTEGER,
                disc_number     INTEGER,
                disc_count      INTEGER,
                duration_ms     INTEGER NOT NULL,
                codec           TEXT NOT NULL,
                sample_rate     INTEGER NOT NULL,
                bits_per_sample INTEGER NOT NULL,
                channels        INTEGER NOT NULL,
                bitrate_kbps    INTEGER NOT NULL,
                has_cover       INTEGER NOT NULL,
                has_lyrics      INTEGER NOT NULL,
                added_utc       INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_tracks_folder ON tracks(folder);
            CREATE INDEX IF NOT EXISTS ix_tracks_album  ON tracks(album_artist, album);
            CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist);

            -- Riwayat putar: satu baris setiap kali sebuah lagu benar-benar didengarkan.
            -- Disambungkan ke lagu lewat path, jadi riwayat tetap ada walaupun lagunya di-scan ulang.
            CREATE TABLE IF NOT EXISTS plays (
                id         INTEGER PRIMARY KEY,
                path       TEXT NOT NULL COLLATE NOCASE,
                played_utc INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_plays_path ON plays(path);
            CREATE INDEX IF NOT EXISTS ix_plays_time ON plays(played_utc);

            -- Hasil pemeriksaan kualitas sumber. Berlaku selama ukuran dan waktu ubah file sama dengan saat diperiksa,
            -- dan versi algoritmanya sama (QualityAnalyzer.Version); kalau tidak, lagunya diperiksa ulang.
            CREATE TABLE IF NOT EXISTS quality_reports (
                path             TEXT PRIMARY KEY COLLATE NOCASE,
                file_size        INTEGER NOT NULL,
                modified_utc     INTEGER NOT NULL,
                analyzer_version INTEGER NOT NULL,
                sample_rate      INTEGER NOT NULL,
                container_bits   INTEGER NOT NULL,
                is_float         INTEGER NOT NULL,
                effective_bits   INTEGER,
                cutoff_hz        REAL,
                verdict          INTEGER NOT NULL,
                source_rate      INTEGER,
                spectrum         BLOB,
                analyzed_utc     INTEGER NOT NULL
            );

            -- Lagu favorit (bintang ☆). Sama seperti riwayat putar, disambungkan lewat path: favorit tetap ada
            -- walaupun lagunya di-scan ulang, atau foldernya sempat dihapus dari library lalu ditambahkan lagi.
            CREATE TABLE IF NOT EXISTS favourites (
                path      TEXT PRIMARY KEY COLLATE NOCASE,
                added_utc INTEGER NOT NULL
            );

            -- Playlist buatan sendiri. Isinya juga disambungkan lewat path; lagu yang sama boleh muncul lebih dari
            -- sekali, jadi setiap baris isi punya id sendiri untuk dihapus atau dipindah.
            CREATE TABLE IF NOT EXISTS playlists (
                id           INTEGER PRIMARY KEY,
                name         TEXT NOT NULL,
                created_utc  INTEGER NOT NULL,
                modified_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS playlist_items (
                id          INTEGER PRIMARY KEY,
                playlist_id INTEGER NOT NULL,
                position    INTEGER NOT NULL,
                path        TEXT NOT NULL COLLATE NOCASE,
                added_utc   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_playlist_items ON playlist_items(playlist_id, position);
            """);

        // Migrasi: setiap perubahan skema menaikkan user_version satu angka
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        long version = (long)versionCommand.ExecuteScalar()!;

        if (version < 1)
        {
            // Versi 1: added_utc dulu berisi waktu scan; sekarang waktu ubah file (lihat UpsertTracks)
            Execute(connection, "UPDATE tracks SET added_utc = modified_utc; PRAGMA user_version = 1;");
        }

        if (version < 2)
        {
            // Versi 2: tanggal rilis lengkap dan copyright untuk halaman album. modified_utc dikosongkan supaya scan
            // berikutnya membaca ulang tag semua lagu; tanggal ditambahkan (added_utc) tidak ikut berubah.
            // Kolom hanya ditambah kalau belum ada, supaya migrasi ini aman kalau sampai dijalankan ulang
            var columns = TableColumns(connection, "tracks");
            if (!columns.Contains("release_date"))
                Execute(connection, "ALTER TABLE tracks ADD COLUMN release_date TEXT;");
            if (!columns.Contains("copyright"))
                Execute(connection, "ALTER TABLE tracks ADD COLUMN copyright TEXT;");
            Execute(connection, "UPDATE tracks SET modified_utc = 0; PRAGMA user_version = 2;");
        }

        if (version < 3)
        {
            // Versi 3: jenis rilis dari tag MusicBrainz (RELEASETYPE) untuk halaman artis. Sama seperti versi 2:
            // scan berikutnya membaca ulang tag semua lagu, tanggal ditambahkan tidak berubah.
            if (!TableColumns(connection, "tracks").Contains("release_type"))
                Execute(connection, "ALTER TABLE tracks ADD COLUMN release_type TEXT;");
            Execute(connection, "UPDATE tracks SET modified_utc = 0; PRAGMA user_version = 3;");
        }
    }

    private static HashSet<string> TableColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static void Set(SqliteCommand command, string name, object? value) =>
        command.Parameters[name].Value = value ?? DBNull.Value;

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private const int TrackColumnCount = 26; // jumlah kolom di TrackColumns; naikkan setiap kali kolom ditambah

    private static List<Track> ReadTracks(SqliteCommand command)
    {
        using var r = command.ExecuteReader();
        var tracks = new List<Track>();
        while (r.Read())
            tracks.Add(ReadTrack(r));
        return tracks;
    }

    private static Track ReadTrack(SqliteDataReader r) =>
            new Track
            {
                Id = r.GetInt64(0),
                FilePath = r.GetString(1),
                FolderPath = r.GetString(2),
                FileSize = r.GetInt64(3),
                ModifiedUtcTicks = r.GetInt64(4),
                Title = Text(r, 5),
                Artist = Text(r, 6),
                Album = Text(r, 7),
                AlbumArtist = Text(r, 8),
                Genre = Text(r, 9),
                ReleaseDate = Text(r, 23),
                Copyright = Text(r, 24),
                ReleaseType = Text(r, 25),
                Year = Number(r, 10),
                TrackNumber = Number(r, 11),
                TrackCount = Number(r, 12),
                DiscNumber = Number(r, 13),
                DiscCount = Number(r, 14),
                Duration = TimeSpan.FromMilliseconds(r.GetInt64(15)),
                Codec = r.GetString(16),
                SampleRate = r.GetInt32(17),
                BitsPerSample = r.GetInt32(18),
                Channels = r.GetInt32(19),
                BitrateKbps = r.GetInt32(20),
                HasCover = r.GetBoolean(21),
                HasLyrics = r.GetBoolean(22),
            };

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
}

namespace MusicPlayer.Library;

/// <summary>Membaca tag dan info audio sebuah file dengan TagLib#.</summary>
public static class TagReader
{
    /// <summary>
    /// Selalu mengembalikan Track. Kalau tag gagal dibaca (file rusak, terkunci, dan sebagainya),
    /// Track tetap dibuat dengan info dasar, dan alasannya dikembalikan lewat <paramref name="error"/>.
    /// </summary>
    public static Track Read(string filePath, FileStamp stamp, out string? error)
    {
        var track = new Track
        {
            FilePath = filePath,
            FolderPath = Path.GetDirectoryName(filePath) ?? "",
            FileSize = stamp.Size,
            ModifiedUtcTicks = stamp.ModifiedUtcTicks,
            Codec = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
        };

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;
            var properties = file.Properties;
            var duration = properties?.Duration ?? TimeSpan.Zero;

            // Untuk beberapa format (misalnya AAC mentah), TagLib# kadang melaporkan bitrate yang
            // tidak masuk akal. Kalau begitu, hitung sendiri dari ukuran file dan durasinya.
            int bitrate = properties?.AudioBitrate ?? 0;
            if ((bitrate <= 0 || bitrate > 10_000) && duration.TotalSeconds > 0)
                bitrate = (int)(stamp.Size * 8 / duration.TotalSeconds / 1000);

            error = null;
            return track with
            {
                Title = Clean(tag.Title),
                Artist = Clean(tag.JoinedPerformers),
                Album = Clean(tag.Album),
                AlbumArtist = Clean(tag.JoinedAlbumArtists),
                Genre = Clean(tag.FirstGenre),
                Year = PositiveOrNull(tag.Year),
                TrackNumber = PositiveOrNull(tag.Track),
                TrackCount = PositiveOrNull(tag.TrackCount),
                DiscNumber = PositiveOrNull(tag.Disc),
                DiscCount = PositiveOrNull(tag.DiscCount),
                Duration = duration,
                SampleRate = properties?.AudioSampleRate ?? 0,
                BitsPerSample = properties?.BitsPerSample ?? 0,
                Channels = properties?.AudioChannels ?? 0,
                BitrateKbps = bitrate,
                HasCover = (tag.Pictures?.Length ?? 0) > 0,
                HasLyrics = !string.IsNullOrWhiteSpace(tag.Lyrics),
            };
        }
        catch (Exception ex)
        {
            // Satu file aneh tidak boleh menggagalkan scan ribuan file lainnya
            error = ex.Message;
            return track;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? PositiveOrNull(uint value) => value > 0 ? (int)value : null;
}

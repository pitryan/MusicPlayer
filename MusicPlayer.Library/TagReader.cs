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
                ReleaseDate = Clean(ReadReleaseDate(file)),
                Copyright = Clean(tag.Copyright),
            };
        }
        catch (Exception ex)
        {
            // Satu file aneh tidak boleh menggagalkan scan ribuan file lainnya
            error = ex.Message;
            return track;
        }
    }

    /// <summary>
    /// Tanggal rilis lengkap kalau ada, misalnya "2026-09-30". Untuk semua format TagLib hanya menyediakan tahunnya,
    /// jadi tanggal lengkap dibaca dari tag aslinya: DATE (FLAC/Ogg), TDRC (MP3), atau ©day (M4A).
    /// </summary>
    // Kotak tanggal rilis di tag M4A bernama "©day" (byte 0xA9 lalu "day"). Konstanta BoxType milik TagLib tidak publik.
    private static readonly TagLib.ByteVector AppleDayBox = new(0xA9, (byte)'d', (byte)'a', (byte)'y');

    private static string? ReadReleaseDate(TagLib.File file)
    {
        // DATE = tanggal rilis edisi ini. ORIGINALDATE (dari MusicBrainz Picard) dan YEAR jadi cadangan; YEAR dari
        // toko musik kadang berisi waktu lengkap seperti "2025-02-06T15:00:00Z", dan bagian tanggalnya tetap terbaca.
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            foreach (string key in new[] { "DATE", "ORIGINALDATE", "YEAR" })
            {
                if (xiph.GetFirstField(key) is { Length: > 0 } date)
                    return date;
            }
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3
            && TagLib.Id3v2.TextInformationFrame.Get(id3, "TDRC", false)?.Text is { Length: > 0 } recorded)
            return recorded[0];

        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple
            && apple.GetText(AppleDayBox) is { Length: > 0 } day)
            return day[0];

        return null;
    }

    // Nama field lirik selain LYRICS: Mp3tag menulis lirik ke UNSYNCEDLYRICS di FLAC/Ogg, dan aplikasi lain kadang
    // menyimpannya di field teks bebas (TXXX) MP3 dengan nama yang sama
    private static readonly string[] LyricsFieldNames = ["LYRICS", "UNSYNCEDLYRICS", "UNSYNCED LYRICS"];

    /// <summary>
    /// Lirik dari tag file, bisa teks biasa atau teks LRC bertanda waktu. Dibaca saat dibutuhkan (bukan saat scan),
    /// supaya database tetap kecil. Null kalau tidak ada atau file tidak bisa dibaca.
    /// </summary>
    public static string? ReadLyrics(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);

            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                foreach (string name in LyricsFieldNames)
                {
                    if (Clean(xiph.GetFirstField(name)) is { } value)
                        return value;
                }
            }

            // USLT (MP3 dan WAV), ©lyr (M4A), dan field lirik format lain
            if (Clean(file.Tag.Lyrics) is { } lyrics)
                return lyrics;

            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                foreach (string name in LyricsFieldNames)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, TagLib.StringType.UTF8, false, false);
                    if (frame?.Text is { Length: > 0 } text && Clean(string.Join("\n", text)) is { } value)
                        return value;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null; // file hilang, rusak, atau terkunci: anggap tidak ada lirik di tag
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? PositiveOrNull(uint value) => value > 0 ? (int)value : null;
}

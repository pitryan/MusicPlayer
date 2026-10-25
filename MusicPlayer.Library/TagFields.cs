namespace MusicPlayer.Library;

/// <summary>Kelompok field di editor tag, sesuai urutan tampilnya.</summary>
public enum TagGroup
{
    Details,
    Release,
    Credits,
    Sorting,
    MusicBrainz,
    Lyrics,
}

/// <summary>Jenis penyimpanan tag di file: Vorbis comment (FLAC, Ogg), ID3v2 (MP3, WAV, AIFF), atau atom MP4 (M4A).</summary>
public enum TagFormat
{
    Vorbis,
    Id3,
    Mp4,
}

/// <summary>
/// Satu field tag. <see cref="Key"/> adalah nama internal MusicBrainz Picard, dan nama penyimpanannya di setiap format
/// mengikuti tag mapping Picard (picard/formats/vorbis.py, id3.py, mp4.py), supaya file yang disunting di sini terbaca
/// sama oleh Picard, foobar2000, dan Mp3tag, dan data dari MusicBrainz bisa ditulis apa adanya.
/// </summary>
/// <param name="Vorbis">Nama field Vorbis, misalnya "ALBUMARTIST".</param>
/// <param name="Id3">Frame ID3: "TPE2", "TXXX:deskripsi", "UFID:pemilik", "TIPL:peran", "COMM", "USLT", "WOAR", atau
/// "TRCK"/"TPOS" untuk nomor.</param>
/// <param name="Mp4">Atom MP4: "aART", "----:nama" (freeform com.apple.iTunes), atau "trkn"/"disk"/"cpil"/"tmpo";
/// null kalau Picard tidak menyimpan field ini di M4A.</param>
/// <param name="MultiValue">Bisa berisi beberapa nilai; di editor dipisahkan dengan "; ".</param>
public sealed record TagField(string Key, string Label, TagGroup Group, string Vorbis, string Id3, string? Mp4, bool MultiValue = false)
{
    public bool IsSupported(TagFormat format) => format != TagFormat.Mp4 || Mp4 is not null;
}

public static class TagFields
{
    /// <summary>Pemisah nilai ganda di editor, sama seperti Picard.</summary>
    public const string Separator = "; ";

    public static readonly IReadOnlyList<TagField> All =
    [
        // ── Details ──
        new("title", "Title", TagGroup.Details, "TITLE", "TIT2", "\u00A9nam"),
        new("artist", "Artist", TagGroup.Details, "ARTIST", "TPE1", "\u00A9ART"),
        new("artists", "Artists", TagGroup.Details, "ARTISTS", "TXXX:ARTISTS", "----:ARTISTS", MultiValue: true),
        new("album", "Album", TagGroup.Details, "ALBUM", "TALB", "\u00A9alb"),
        new("albumartist", "Album Artist", TagGroup.Details, "ALBUMARTIST", "TPE2", "aART"),
        new("date", "Release Date", TagGroup.Details, "DATE", "TDRC", "\u00A9day"),
        new("originaldate", "Original Release Date", TagGroup.Details, "ORIGINALDATE", "TDOR", null),
        new("tracknumber", "Track", TagGroup.Details, "TRACKNUMBER", "TRCK", "trkn"),
        new("totaltracks", "Total Tracks", TagGroup.Details, "TRACKTOTAL", "TRCK", "trkn"),
        new("discnumber", "Disc", TagGroup.Details, "DISCNUMBER", "TPOS", "disk"),
        new("totaldiscs", "Total Discs", TagGroup.Details, "DISCTOTAL", "TPOS", "disk"),
        new("discsubtitle", "Disc Subtitle", TagGroup.Details, "DISCSUBTITLE", "TSST", "----:DISCSUBTITLE"),
        new("genre", "Genre", TagGroup.Details, "GENRE", "TCON", "\u00A9gen", MultiValue: true),
        new("compilation", "Compilation", TagGroup.Details, "COMPILATION", "TCMP", "cpil"),
        new("bpm", "BPM", TagGroup.Details, "BPM", "TBPM", "tmpo"),
        new("subtitle", "Subtitle", TagGroup.Details, "SUBTITLE", "TIT3", "----:SUBTITLE"),
        new("grouping", "Grouping", TagGroup.Details, "GROUPING", "TIT1", "\u00A9grp"),
        new("mood", "Mood", TagGroup.Details, "MOOD", "TMOO", "----:MOOD"),
        new("comment", "Comment", TagGroup.Details, "COMMENT", "COMM", "\u00A9cmt"),

        // ── Release ──
        new("releasetype", "Release Type", TagGroup.Release, "RELEASETYPE", "TXXX:MusicBrainz Album Type", "----:MusicBrainz Album Type", MultiValue: true),
        new("releasestatus", "Release Status", TagGroup.Release, "RELEASESTATUS", "TXXX:MusicBrainz Album Status", "----:MusicBrainz Album Status"),
        new("releasecountry", "Release Country", TagGroup.Release, "RELEASECOUNTRY", "TXXX:MusicBrainz Album Release Country", "----:MusicBrainz Album Release Country"),
        new("label", "Record Label", TagGroup.Release, "LABEL", "TPUB", "----:LABEL", MultiValue: true),
        new("catalognumber", "Catalogue Number", TagGroup.Release, "CATALOGNUMBER", "TXXX:CATALOGNUMBER", "----:CATALOGNUMBER", MultiValue: true),
        new("barcode", "Barcode", TagGroup.Release, "BARCODE", "TXXX:BARCODE", "----:BARCODE"),
        new("asin", "ASIN", TagGroup.Release, "ASIN", "TXXX:ASIN", "----:ASIN"),
        new("media", "Media", TagGroup.Release, "MEDIA", "TMED", "----:MEDIA"),
        new("script", "Script", TagGroup.Release, "SCRIPT", "TXXX:SCRIPT", "----:SCRIPT"),
        new("language", "Language", TagGroup.Release, "LANGUAGE", "TLAN", "----:LANGUAGE"),
        new("copyright", "Copyright", TagGroup.Release, "COPYRIGHT", "TCOP", "cprt"),

        // ── Credits ──
        new("composer", "Composer", TagGroup.Credits, "COMPOSER", "TCOM", "\u00A9wrt", MultiValue: true),
        new("lyricist", "Lyricist", TagGroup.Credits, "LYRICIST", "TEXT", "----:LYRICIST", MultiValue: true),
        new("arranger", "Arranger", TagGroup.Credits, "ARRANGER", "TIPL:arranger", null, MultiValue: true),
        new("producer", "Producer", TagGroup.Credits, "PRODUCER", "TIPL:producer", "----:PRODUCER", MultiValue: true),
        new("engineer", "Engineer", TagGroup.Credits, "ENGINEER", "TIPL:engineer", "----:ENGINEER", MultiValue: true),
        new("mixer", "Mix Engineer", TagGroup.Credits, "MIXER", "TIPL:mix", "----:MIXER", MultiValue: true),
        new("djmixer", "DJ Mixer", TagGroup.Credits, "DJMIXER", "TIPL:DJ-mix", "----:DJMIXER", MultiValue: true),
        new("remixer", "Remixer", TagGroup.Credits, "REMIXER", "TPE4", "----:REMIXER", MultiValue: true),
        new("conductor", "Conductor", TagGroup.Credits, "CONDUCTOR", "TPE3", "----:CONDUCTOR", MultiValue: true),
        new("encodedby", "Encoded By", TagGroup.Credits, "ENCODEDBY", "TENC", "\u00A9too"),
        new("website", "Website", TagGroup.Credits, "WEBSITE", "WOAR", null),

        // ── Sorting ──
        new("titlesort", "Title Sort Order", TagGroup.Sorting, "TITLESORT", "TSOT", "sonm"),
        new("artistsort", "Artist Sort Order", TagGroup.Sorting, "ARTISTSORT", "TSOP", "soar"),
        new("albumsort", "Album Sort Order", TagGroup.Sorting, "ALBUMSORT", "TSOA", "soal"),
        new("albumartistsort", "Album Artist Sort Order", TagGroup.Sorting, "ALBUMARTISTSORT", "TSO2", "soaa"),
        new("composersort", "Composer Sort Order", TagGroup.Sorting, "COMPOSERSORT", "TSOC", "soco"),

        // ── MusicBrainz (Picard: musicbrainz_trackid = id lagu di rilis, musicbrainz_recordingid = id rekaman) ──
        new("musicbrainz_recordingid", "Recording ID", TagGroup.MusicBrainz, "MUSICBRAINZ_TRACKID", "UFID:http://musicbrainz.org", "----:MusicBrainz Track Id"),
        new("musicbrainz_trackid", "Track ID", TagGroup.MusicBrainz, "MUSICBRAINZ_RELEASETRACKID", "TXXX:MusicBrainz Release Track Id", "----:MusicBrainz Release Track Id"),
        new("musicbrainz_albumid", "Release ID", TagGroup.MusicBrainz, "MUSICBRAINZ_ALBUMID", "TXXX:MusicBrainz Album Id", "----:MusicBrainz Album Id"),
        new("musicbrainz_releasegroupid", "Release Group ID", TagGroup.MusicBrainz, "MUSICBRAINZ_RELEASEGROUPID", "TXXX:MusicBrainz Release Group Id", "----:MusicBrainz Release Group Id"),
        new("musicbrainz_artistid", "Artist ID", TagGroup.MusicBrainz, "MUSICBRAINZ_ARTISTID", "TXXX:MusicBrainz Artist Id", "----:MusicBrainz Artist Id", MultiValue: true),
        new("musicbrainz_albumartistid", "Album Artist ID", TagGroup.MusicBrainz, "MUSICBRAINZ_ALBUMARTISTID", "TXXX:MusicBrainz Album Artist Id", "----:MusicBrainz Album Artist Id", MultiValue: true),
        new("musicbrainz_workid", "Work ID", TagGroup.MusicBrainz, "MUSICBRAINZ_WORKID", "TXXX:MusicBrainz Work Id", "----:MusicBrainz Work Id", MultiValue: true),
        new("acoustid_id", "AcoustID", TagGroup.MusicBrainz, "ACOUSTID_ID", "TXXX:Acoustid Id", "----:Acoustid Id"),
        new("isrc", "ISRC", TagGroup.MusicBrainz, "ISRC", "TSRC", "----:ISRC", MultiValue: true),

        // ── Lyrics ──
        new("lyrics", "Lyrics", TagGroup.Lyrics, "LYRICS", "USLT", "\u00A9lyr"),
        // Cara baca dan terjemahan lirik, ditulis manual di editor lirik: satu baris per baris lirik di LYRICS (urutan
        // baris setelah dibaca LrcParser). Nama yang sama di semua format, seperti tag kustom Picard.
        new("lyrics_reading", "Lyrics Reading", TagGroup.Lyrics, "LYRICS_READING", "TXXX:LYRICS_READING", "----:LYRICS_READING"),
        new("lyrics_translation", "Lyrics Translation", TagGroup.Lyrics, "LYRICS_TRANSLATION", "TXXX:LYRICS_TRANSLATION", "----:LYRICS_TRANSLATION"),
    ];

    private static readonly Dictionary<string, TagField> ByKey = All.ToDictionary(f => f.Key);

    public static TagField Get(string key) => ByKey[key];

    public static IReadOnlyList<string> Split(string value, bool multiValue) =>
        multiValue
            ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : value.Trim().Length > 0 ? [value.Trim()] : [];
}

using System.Globalization;
using TagLib;
using TagLib.Id3v2;

namespace MusicPlayer.Library;

/// <summary>Satu gambar di tag (cover depan, belakang, dan sebagainya).</summary>
public sealed record TagPicture(PictureType Type, string MimeType, byte[] Data, string Description = "");

/// <summary>
/// Isi tag satu file untuk editor: nilai per field (lihat <see cref="TagFields"/>), gambar-gambarnya, dan durasi
/// audionya (untuk mencocokkan file dengan lagu di MusicBrainz).
/// </summary>
public sealed record TagValues(TagFormat Format, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<TagPicture> Pictures)
{
    public TimeSpan Duration { get; init; }

    public string Get(string key) => Fields.TryGetValue(key, out var value) ? value : "";
}

/// <summary>
/// Membaca dan menulis semua field di <see cref="TagFields"/> dengan nama penyimpanan Picard. Penulisan dilakukan pada
/// salinan file dulu, lalu salinan itu menggantikan file asli; kalau aplikasi tertutup di tengah jalan, file musiknya
/// tidak pernah setengah tertulis. Tag MP3/WAV disimpan sebagai ID3v2.4 (bawaan Picard), karena ID3v2.3 tidak bisa
/// menyimpan daftar kredit (arranger, producer, ...) dengan benar lewat TagLib. Tag lama yang sudah ada di file (ID3v1,
/// APE, RIFF INFO) ikut diperbarui, karena library juga membacanya (lihat LegacyTypes).
/// </summary>
public static class TagEditor
{
    private const string AppleMean = "com.apple.iTunes";

    /// <summary>Isi tag sebuah file. Field yang kosong tidak ada di <see cref="TagValues.Fields"/>.</summary>
    public static TagValues Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var format = FormatOf(file);
        var fields = new Dictionary<string, string>();
        // Nilai yang hanya ada di tag lama juga dibaca library, jadi ikut ditampilkan (lihat LegacyTypes)
        var legacy = LegacyTags(file, format) is { Count: > 0 } tags ? new CombinedTag(tags.ToArray()) : null;

        foreach (var field in TagFields.All.Where(f => f.IsSupported(format)))
        {
            var values = format switch
            {
                TagFormat.Vorbis => ReadVorbis((TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true), field),
                TagFormat.Mp4 => ReadMp4((TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true), field),
                _ => ReadId3((TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true), field),
            };
            var clean = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(field.Trim).ToList();
            if (clean.Count == 0 && legacy is not null && LegacyFields.TryGetValue(field.Key, out var common))
                clean = common.Read(legacy).Where(v => !string.IsNullOrWhiteSpace(v)).Select(field.Trim).ToList();
            if (clean.Count > 0)
                fields[field.Key] = string.Join(TagFields.Separator, clean);
        }

        var pictures = file.Tag.Pictures
            .Select(p => new TagPicture(p.Type, p.MimeType ?? "", p.Data.Data, p.Description ?? ""))
            .ToList();
        return new TagValues(format, fields, pictures) { Duration = file.Properties?.Duration ?? TimeSpan.Zero };
    }

    /// <summary>
    /// Menulis perubahan. Hanya field di <paramref name="changes"/> yang disentuh; nilai kosong menghapus field itu.
    /// <paramref name="pictures"/> null berarti gambar tidak diubah, daftar kosong berarti semua gambar dihapus.
    /// Dengan <paramref name="frontCoverOnly"/>, <paramref name="pictures"/> hanya menggantikan cover depan: gambar lain
    /// (cover belakang, booklet, foto artis) tetap di file (lihat CoverSlots).
    /// Field yang tidak didukung format file ini (misalnya Arranger di M4A) dilewati.
    /// </summary>
    public static void Write(string path, IReadOnlyDictionary<string, string> changes, IReadOnlyList<TagPicture>? pictures = null,
        bool frontCoverOnly = false)
    {
        // File read-only ditolak sebelum disalin: File.Replace juga tidak bisa menimpanya, dan salinannya ikut
        // read-only sehingga tertinggal. Pesannya menyebut lagunya, bukan nama salinan sementara.
        if (System.IO.File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException($"\"{Path.GetFileName(path)}\" is read-only.");

        // File MP4 berekstensi lain (misalnya M4A yang dinamai .mp3 atau .aac) dibaca editor dan library sesuai ekstensinya:
        // tag MP4 yang ditulis ke sana tidak pernah terbaca, dan tag ID3 di depan box "ftyp" membuat file tidak bisa
        // diputar. Jadi ditolak tanpa diubah, dengan pesan yang jelas.
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        bool mp4 = extension is "m4a" or "mp4" or "m4b";
        if (!mp4 && StartsWithFtyp(path))
            throw new InvalidDataException(
                $"\"{Path.GetFileName(path)}\" is an M4A file with a .{extension} extension. Rename it to .m4a to edit its tags.");

        // Ditulis ke salinan di folder yang sama (supaya penggantian akhirnya atomik), bernama tersembunyi dan
        // berakhiran .tmp, jadi tidak ikut terbaca scan library kalau sampai tertinggal. Namanya pendek dan tidak memuat
        // nama lagu: nama file di NTFS paling panjang 255 karakter, dan lagu yang namanya hampir sepanjang itu juga
        // harus bisa disimpan.
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        System.IO.File.Copy(path, temporary);
        bool complete = false;
        try
        {
            if (OperatingSystem.IsWindows())
                System.IO.File.SetAttributes(temporary, System.IO.File.GetAttributes(temporary) | FileAttributes.Hidden);

            // TagLib memilih pembaca dari ekstensi, jadi jenis file asli diberikan langsung (.aac adalah AAC mentah/ADTS,
            // yang dibaca TagLib seperti MP3). File MP4 dibuka lewat turunan kelasnya sendiri, supaya atom freeform bisa
            // ditulis dengan struktur yang benar (lihat Mp4File).
            using (var file = mp4
                       ? new Mp4File(temporary)
                       : TagLib.File.Create(temporary, "taglib/" + extension, ReadStyle.Average))
            {
                Apply(file, changes, pictures, frontCoverOnly);
                file.Save();
            }

            if (OperatingSystem.IsWindows())
                System.IO.File.SetAttributes(temporary, System.IO.File.GetAttributes(temporary) & ~FileAttributes.Hidden);
            System.IO.File.Replace(temporary, path, null);
        }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070498) or unchecked((int)0x80070499))
        {
            // Hanya ERROR_UNABLE_TO_MOVE_REPLACEMENT(_2) yang berarti File.Replace sendiri sudah menghapus file asli.
            // Kalau file asli hilang karena aplikasi lain (dipindah atau dihapus), salinannya tidak boleh dikembalikan.
            complete = true;
            throw;
        }
        finally
        {
            CleanUpTemporary(temporary, path, complete);
        }
    }

    /// <summary>
    /// Membereskan salinan sementara dari <see cref="Write"/> kalau masih ada: biasanya dihapus. Tapi File.Replace bisa
    /// gagal setelah file asli terhapus (ERROR_UNABLE_TO_MOVE_REPLACEMENT, misalnya antivirus sedang membuka salinannya);
    /// kalau file asli sudah tidak ada dan <paramref name="complete"/> (Replace gagal dengan error itu), salinan itu
    /// satu-satunya isi lagu, jadi dikembalikan ke nama aslinya. Tidak pernah melempar exception, supaya error yang
    /// sebenarnya tidak tertutup.
    /// </summary>
    public static void CleanUpTemporary(string temporary, string path, bool complete)
    {
        try
        {
            if (!System.IO.File.Exists(temporary))
                return;
            if (complete && !System.IO.File.Exists(path))
            {
                System.IO.File.Move(temporary, path); // tanpa overwrite: file asli yang ternyata masih ada tidak ditimpa
                return;
            }

            // Atribut dibuang dulu, karena file read-only tidak bisa dihapus
            System.IO.File.SetAttributes(temporary, FileAttributes.Normal);
            System.IO.File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Salinan tertinggal; namanya berakhiran .tmp, jadi tidak ikut terbaca scan library
        }
    }

    /// <summary>Isi file ini MP4: diawali box "ftyp" (4 byte ukuran, lalu jenis box).</summary>
    private static bool StartsWithFtyp(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header[4..].SequenceEqual("ftyp"u8);
    }

    public static TagFormat FormatOf(TagLib.File file) => file switch
    {
        TagLib.Flac.File or TagLib.Ogg.File => TagFormat.Vorbis,
        TagLib.Mpeg4.File => TagFormat.Mp4,
        _ => TagFormat.Id3,
    };

    private static void Apply(TagLib.File file, IReadOnlyDictionary<string, string> changes, IReadOnlyList<TagPicture>? pictures,
        bool frontCoverOnly)
    {
        var format = FormatOf(file);
        if (format == TagFormat.Id3)
        {
            TagLib.Id3v2.Tag.DefaultVersion = 4;
            TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        }

        // Tag lama yang hanya dibuat TagLib di memori tidak ikut disimpan: di MP3 itu ID3v1 berisi salinan nilai ID3v2
        // sebelum diubah, yang muncul lagi di library begitu field-nya dikosongkan (di WAV: INFO, "MID ", dan DivX)
        foreach (var type in LegacyTypes.Where(t => t != MainType(format) && !file.TagTypesOnDisk.HasFlag(t)))
            file.RemoveTags(type);
        var legacy = LegacyTags(file, format);
        var main = file.GetTag(MainType(format), true);

        foreach (var (key, value) in changes)
        {
            var field = TagFields.Get(key);
            if (!field.IsSupported(format))
                continue;

            var values = TagFields.Split(value, field);
            switch (format)
            {
                case TagFormat.Vorbis:
                    WriteVorbis((TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true), field, values);
                    break;
                case TagFormat.Mp4:
                    WriteMp4((TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true), (Mp4File)file, field, values);
                    break;
                default:
                    WriteId3((TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true), field, values, legacy);
                    break;
            }

            // Field yang sama di tag lama ikut diganti atau dihapus, supaya nilai lamanya tidak muncul lagi
            if (LegacyFields.TryGetValue(key, out var common))
            {
                foreach (var tag in legacy)
                    common.Copy(main, tag);
            }
        }

        if (pictures is not null)
        {
            var written = pictures
                .Select(p => (IPicture)new Picture(new ByteVector(p.Data)) { Type = p.Type, MimeType = p.MimeType, Description = p.Description });
            if (frontCoverOnly)
            {
                // Cover baru di depan (pemutar lain umumnya memakai gambar pertama), gambar lain dipertahankan
                var existing = file.Tag.Pictures ?? [];
                var slots = CoverSlots(existing.Select(p => p.Type).ToList());
                written = written.Concat(existing.Where((_, index) => !slots[index]));
            }
            file.Tag.Pictures = written.ToArray();
        }
    }

    /// <summary>
    /// Gambar mana yang dianggap cover depan: yang bertipe FrontCover, atau (kalau tidak ada satu pun) yang bertipe
    /// Other, karena banyak tagger menyimpan cover depan sebagai Other. Gambar lain (cover belakang, booklet, foto
    /// artis, ...) bukan cover depan dan tidak tersentuh saat cover diganti.
    /// </summary>
    public static bool[] CoverSlots(IReadOnlyList<PictureType> types)
    {
        bool hasFront = types.Contains(PictureType.FrontCover);
        return types.Select(t => t == PictureType.FrontCover || (!hasFront && t == PictureType.Other)).ToArray();
    }

    // ── Tag lama ────────────────────────────────────────────
    // Selain tag utama yang disunting editor, file bisa membawa tag lain: ID3v1 dan APE di MP3/AAC, RIFF INFO di WAV,
    // ID3 di FLAC. TagReader membaca tag gabungan TagLib (file.Tag), yang mengambil field kosong di tag utama dari tag
    // lain itu. Tag lama tidak dihapus, karena ada pemutar dan aplikasi yang hanya membacanya (ID3v1 di pemutar lama,
    // INFO di Windows Explorer), dan APE bisa berisi data aplikasi lain (ReplayGain dari mp3gain). Sebagai gantinya,
    // field yang ditulis editor ikut ditulis ke tag lama, dan nilai yang hanya ada di tag lama ikut ditampilkan editor.
    // Nilai yang tidak muat di tag lama (album artist dan lirik di ID3v1, judul lebih dari 30 karakter, huruf non-Latin)
    // terpotong atau dilewati di sana saja; library tetap membaca tag utama lebih dulu.

    // Urutannya sama dengan urutan baca TagLib, supaya nilai yang ditampilkan sama dengan yang dibaca library
    private static readonly TagTypes[] LegacyTypes =
        [TagTypes.Id3v2, TagTypes.Ape, TagTypes.Id3v1, TagTypes.RiffInfo, TagTypes.MovieId, TagTypes.DivX];

    private static TagTypes MainType(TagFormat format) => format switch
    {
        TagFormat.Vorbis => TagTypes.Xiph,
        TagFormat.Mp4 => TagTypes.Apple,
        _ => TagTypes.Id3v2,
    };

    /// <summary>Tag lama yang memang ada di file, bukan yang hanya dibuat TagLib di memori.</summary>
    private static List<TagLib.Tag> LegacyTags(TagLib.File file, TagFormat format) =>
        LegacyTypes
            .Where(type => type != MainType(format) && file.TagTypesOnDisk.HasFlag(type))
            .Select(type => file.GetTag(type, false))
            .OfType<TagLib.Tag>()
            .ToList();

    /// <summary>
    /// Field editor yang juga bisa ada di tag lama, lewat properti umum TagLib: Read membacanya dari tag lama, Copy
    /// menyalinnya dari tag utama ke tag lama. Isinya semua field yang dibaca TagReader, ditambah komentar dan komposer
    /// yang juga disimpan ID3v1 atau INFO.
    /// </summary>
    private static readonly Dictionary<string, (Func<TagLib.Tag, string[]> Read, Action<TagLib.Tag, TagLib.Tag> Copy)> LegacyFields = new()
    {
        ["title"] = (t => [t.Title ?? ""], (from, to) => to.Title = from.Title),
        ["artist"] = (t => t.Performers ?? [], (from, to) => to.Performers = from.Performers),
        ["album"] = (t => [t.Album ?? ""], (from, to) => to.Album = from.Album),
        ["albumartist"] = (t => t.AlbumArtists ?? [], (from, to) => to.AlbumArtists = from.AlbumArtists),
        ["date"] = (t => Number(t.Year), (from, to) => to.Year = from.Year),
        ["tracknumber"] = (t => Number(t.Track), (from, to) => to.Track = from.Track),
        // Total disalin bersama nomornya: APE menulis "nomor/total" dengan nomornya sendiri, yang bisa masih kosong
        ["totaltracks"] = (t => Number(t.TrackCount), (from, to) => (to.Track, to.TrackCount) = (from.Track, from.TrackCount)),
        ["discnumber"] = (t => Number(t.Disc), (from, to) => to.Disc = from.Disc),
        ["totaldiscs"] = (t => Number(t.DiscCount), (from, to) => (to.Disc, to.DiscCount) = (from.Disc, from.DiscCount)),
        ["genre"] = (t => t.Genres ?? [], (from, to) => to.Genres = from.Genres),
        ["comment"] = (t => [t.Comment ?? ""], (from, to) => to.Comment = from.Comment),
        ["composer"] = (t => t.Composers ?? [], (from, to) => to.Composers = from.Composers),
        ["copyright"] = (t => [t.Copyright ?? ""], (from, to) => to.Copyright = from.Copyright),
        ["lyrics"] = (t => [t.Lyrics ?? ""], (from, to) => to.Lyrics = from.Lyrics),
        ["releasetype"] = (t => [t.MusicBrainzReleaseType ?? ""], (from, to) => to.MusicBrainzReleaseType = ReleaseTypeOf(from)),
    };

    /// <summary>
    /// Jenis rilis di tag utama. TagLib membaca jenis rilis Vorbis dari MUSICBRAINZ_ALBUMTYPE, sedangkan editor (seperti
    /// Picard) menulis RELEASETYPE; nilai yang baru ditulis itu yang disalin ke tag lama. Beberapa nilai digabung dengan
    /// ";", yang dipisah lagi oleh TagLib di ID3v2 dan APE.
    /// </summary>
    private static string? ReleaseTypeOf(TagLib.Tag tag) =>
        tag is TagLib.Ogg.XiphComment xiph
            ? (xiph.GetField("RELEASETYPE") is { Length: > 0 } types ? string.Join(";", types) : null)
            : tag.MusicBrainzReleaseType;

    // ── Vorbis (FLAC, Ogg) ──────────────────────────────────

    private static string[] ReadVorbis(TagLib.Ogg.XiphComment tag, TagField field) => field.Key switch
    {
        // Picard menulis TRACKTOTAL dan TOTALTRACKS; aplikasi lain kadang hanya salah satunya
        "totaltracks" => First(tag.GetField("TRACKTOTAL"), tag.GetField("TOTALTRACKS")),
        "totaldiscs" => First(tag.GetField("DISCTOTAL"), tag.GetField("TOTALDISCS")),
        // Mp3tag menulis lirik ke UNSYNCEDLYRICS
        "lyrics" => First(tag.GetField("LYRICS"), tag.GetField("UNSYNCEDLYRICS")),
        // Nomor "3/12" (gaya lama) dibaca angka depannya saja
        "tracknumber" or "discnumber" => tag.GetField(field.Vorbis).Select(v => v.Split('/')[0]).ToArray(),
        _ => tag.GetField(field.Vorbis),
    };

    private static void WriteVorbis(TagLib.Ogg.XiphComment tag, TagField field, IReadOnlyList<string> values)
    {
        string[] names = field.Key switch
        {
            "totaltracks" => ["TRACKTOTAL", "TOTALTRACKS"],
            "totaldiscs" => ["DISCTOTAL", "TOTALDISCS"],
            "lyrics" => ["LYRICS"],
            _ => [field.Vorbis],
        };
        foreach (string name in names)
        {
            if (values.Count == 0)
                tag.RemoveField(name);
            else
                tag.SetField(name, values.ToArray());
        }

        // Lirik lama dari Mp3tag diganti, supaya tidak ada dua versi lirik yang berbeda di satu file
        if (field.Key == "lyrics")
            tag.RemoveField("UNSYNCEDLYRICS");
    }

    // ── ID3v2 (MP3, WAV, AIFF) ──────────────────────────────

    // Nama TXXX berisi lirik yang juga dibaca TagReader.ReadLyrics kalau tidak ada USLT (foobar2000 dan plugin liriknya
    // menulis TXXX:LYRICS)
    private static readonly string[] Id3LyricsNames = ["LYRICS", "UNSYNCEDLYRICS", "UNSYNCED LYRICS"];

    private static string[] ReadId3(TagLib.Id3v2.Tag tag, TagField field)
    {
        string id = field.Id3;
        switch (field.Key)
        {
            case "tracknumber": return Number(tag.Track);
            case "totaltracks": return Number(tag.TrackCount);
            case "discnumber": return Number(tag.Disc);
            case "totaldiscs": return Number(tag.DiscCount);
            // Nomor genre ("17", "(17)" dari ID3v1 atau tagger lama) dibaca sebagai namanya, seperti oleh library
            case "genre": return tag.Genres ?? [];
            case "comment": return [tag.Comment ?? ""];
            case "lyrics":
                if (!string.IsNullOrWhiteSpace(tag.Lyrics))
                    return [tag.Lyrics];
                return Id3LyricsNames
                    .Select(name => string.Join("\n", UserTextInformationFrame.Get(tag, name, StringType.UTF8, false, false)?.Text ?? []))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Take(1)
                    .ToArray();
            case "compilation": return tag.IsCompilation ? ["1"] : [];
        }

        if (id.StartsWith("TXXX:", StringComparison.Ordinal))
            return UserTextInformationFrame.Get(tag, id[5..], StringType.UTF8, false, false)?.Text ?? [];
        if (id.StartsWith("UFID:", StringComparison.Ordinal))
            return UniqueFileIdentifierFrame.Get(tag, id[5..], false) is { } ufid ? [ufid.Identifier.ToString(StringType.Latin1)] : [];
        if (id.StartsWith("TIPL:", StringComparison.Ordinal))
            return ReadPeople(tag).Where(p => p.Role == id[5..]).Select(p => p.Name).ToArray();
        if (id == "WOAR")
            return tag.GetFrames<UrlLinkFrame>("WOAR").SelectMany(f => f.Text).ToArray();

        return TextInformationFrame.Get(tag, id, false)?.Text ?? [];
    }

    /// <param name="legacy">Tag lama di file (lihat LegacyTags), asal nomor track/disc yang ditampilkan editor.</param>
    private static void WriteId3(TagLib.Id3v2.Tag tag, TagField field, IReadOnlyList<string> values, List<TagLib.Tag> legacy)
    {
        string id = field.Id3;
        string? single = values.Count > 0 ? values[0] : null;
        switch (field.Key)
        {
            case "tracknumber": SetNumberPair(tag, "TRCK", ParseNumber(single), null); return;
            case "totaltracks": SetNumberPair(tag, "TRCK", null, ParseNumber(single), new CombinedTag(legacy.ToArray()).Track); return;
            case "discnumber": SetNumberPair(tag, "TPOS", ParseNumber(single), null); return;
            case "totaldiscs": SetNumberPair(tag, "TPOS", null, ParseNumber(single), new CombinedTag(legacy.ToArray()).Disc); return;
            case "comment": SetComment(tag, single); return;
            case "lyrics": SetLyrics(tag, single); return;
            case "compilation": tag.IsCompilation = IsTrue(single); return;
        }

        if (id.StartsWith("TXXX:", StringComparison.Ordinal))
        {
            if (UserTextInformationFrame.Get(tag, id[5..], StringType.UTF8, false, false) is { } existing)
                tag.RemoveFrame(existing);
            if (values.Count > 0)
                UserTextInformationFrame.Get(tag, id[5..], StringType.UTF8, true, false).Text = values.ToArray();
        }
        else if (id.StartsWith("UFID:", StringComparison.Ordinal))
        {
            if (UniqueFileIdentifierFrame.Get(tag, id[5..], false) is { } existing)
                tag.RemoveFrame(existing);
            if (single is not null)
                UniqueFileIdentifierFrame.Get(tag, id[5..], true).Identifier = ByteVector.FromString(single, StringType.Latin1);
        }
        else if (id.StartsWith("TIPL:", StringComparison.Ordinal))
        {
            string role = id[5..];
            var people = ReadPeople(tag).Where(p => p.Role != role).ToList();
            people.AddRange(values.Select(name => (role, name)));
            tag.RemoveFrames("TIPL");
            tag.RemoveFrames("IPLS");
            if (people.Count > 0)
                TextInformationFrame.Get(tag, "TIPL", StringType.UTF8, true).Text = people.SelectMany(p => new[] { p.Role, p.Name }).ToArray();
        }
        else if (id == "WOAR")
        {
            tag.RemoveFrames("WOAR");
            foreach (string url in values)
            {
                var frame = new UrlLinkFrame("WOAR") { Text = [url] };
                tag.AddFrame(frame);
            }
        }
        else
        {
            tag.RemoveFrames(id);
            if (values.Count > 0)
                TextInformationFrame.Get(tag, id, StringType.UTF8, true).Text = values.ToArray();
        }
    }

    /// <summary>
    /// Lirik ditulis sebagai satu-satunya USLT, dengan bahasa lirik yang diganti. Properti Lyrics milik TagLib hanya
    /// mengganti frame berbahasa sistem ("eng"), jadi lirik berbahasa lain ("jpn", "XXX") tertinggal sebagai frame
    /// pertama, yang dibaca pemutar lain. Lirik di TXXX dari aplikasi lain juga dihapus, karena library membacanya
    /// kalau USLT kosong.
    /// </summary>
    private static void SetLyrics(TagLib.Id3v2.Tag tag, string? lyrics)
    {
        string language = UnsynchronisedLyricsFrame.GetPreferred(tag, "", TagLib.Id3v2.Tag.Language)?.Language is { Length: 3 } shown
            ? shown
            : TagLib.Id3v2.Tag.Language;
        tag.RemoveFrames("USLT");
        foreach (string name in Id3LyricsNames)
        {
            while (UserTextInformationFrame.Get(tag, name, StringType.UTF8, false, false) is { } frame)
                tag.RemoveFrame(frame);
        }

        if (lyrics is not null)
            UnsynchronisedLyricsFrame.Get(tag, "", language, true).Text = lyrics;
    }

    /// <summary>
    /// Komentar menggantikan frame COMM yang tampil di editor dan semua COMM tanpa deskripsi (bahasa apa pun), dengan
    /// alasan yang sama seperti lirik. COMM berdeskripsi milik aplikasi lain (iTunNORM dari iTunes, ...) tetap ada.
    /// </summary>
    private static void SetComment(TagLib.Id3v2.Tag tag, string? comment)
    {
        if (comment is not null)
        {
            var shown = CommentsFrame.GetPreferred(tag, "", TagLib.Id3v2.Tag.Language);
            foreach (var frame in tag.GetFrames<CommentsFrame>().Where(f => f == shown || string.IsNullOrEmpty(f.Description)).ToList())
                tag.RemoveFrame(frame);
        }

        // Nilai kosong: TagLib menghapus semua COMM yang bisa tampil sebagai komentar
        tag.Comment = comment;
    }

    /// <summary>
    /// TRCK dan TPOS ditulis langsung sebagai "3/12" seperti Picard; properti Track milik TagLib menulis "03/12".
    /// Hanya bagian yang diberikan yang diganti (nomor atau totalnya), bagian lainnya diambil dari frame yang ada.
    /// </summary>
    /// <param name="legacyNumber">
    /// Nomor dari tag lama, dipakai kalau frame ini belum punya nomor: editor menampilkan nomor itu, jadi total yang diisi
    /// pengguna tetap tersimpan (total tanpa nomor tidak bisa disimpan di frame ini).
    /// </param>
    private static void SetNumberPair(TagLib.Id3v2.Tag tag, string frameId, uint? number, uint? total, uint legacyNumber = 0)
    {
        string[] current = (TextInformationFrame.Get(tag, frameId, false)?.Text.FirstOrDefault() ?? "").Split('/');
        uint n = number ?? (ParseNumber(current[0]) is > 0 and var existing ? existing : legacyNumber);
        uint t = total ?? (current.Length > 1 ? ParseNumber(current[1]) : 0);

        tag.RemoveFrames(frameId);
        if (n == 0)
            return; // jumlah tanpa nomor tidak bisa disimpan di frame ini

        string text = t > 0 ? $"{n.ToString(CultureInfo.InvariantCulture)}/{t.ToString(CultureInfo.InvariantCulture)}" : n.ToString(CultureInfo.InvariantCulture);
        TextInformationFrame.Get(tag, frameId, StringType.Latin1, true).Text = [text];
    }

    /// <summary>Daftar kredit ID3 (TIPL di v2.4, IPLS di v2.3): pasangan peran dan nama.</summary>
    private static List<(string Role, string Name)> ReadPeople(TagLib.Id3v2.Tag tag)
    {
        var people = new List<(string, string)>();
        foreach (var frame in tag.GetFrames<TextInformationFrame>().Where(f => f.FrameId == "TIPL" || f.FrameId == "IPLS"))
        {
            var text = frame.Text;
            for (int i = 0; i + 1 < text.Length; i += 2)
                people.Add((text[i], text[i + 1]));
        }
        return people;
    }

    // ── MP4 (M4A) ───────────────────────────────────────────

    private static string[] ReadMp4(TagLib.Mpeg4.AppleTag tag, TagField field)
    {
        string atom = field.Mp4!;
        switch (field.Key)
        {
            case "tracknumber": return Number(tag.Track);
            case "totaltracks": return Number(tag.TrackCount);
            case "discnumber": return Number(tag.Disc);
            case "totaldiscs": return Number(tag.DiscCount);
            case "compilation": return tag.IsCompilation ? ["1"] : [];
            case "bpm": return Number(tag.BeatsPerMinute);
        }

        return atom.StartsWith("----:", StringComparison.Ordinal)
            ? tag.GetDashBoxes(AppleMean, atom[5..]) ?? []
            : tag.GetText(Atom(atom));
    }

    private static void WriteMp4(TagLib.Mpeg4.AppleTag tag, Mp4File file, TagField field, IReadOnlyList<string> values)
    {
        string atom = field.Mp4!;
        string? single = values.Count > 0 ? values[0] : null;
        switch (field.Key)
        {
            case "tracknumber": tag.Track = ParseNumber(single); return;
            case "totaltracks": tag.TrackCount = ParseNumber(single); return;
            case "discnumber": tag.Disc = ParseNumber(single); return;
            case "totaldiscs": tag.DiscCount = ParseNumber(single); return;
            case "compilation": tag.IsCompilation = IsTrue(single); return;
            case "bpm": tag.BeatsPerMinute = ParseNumber(single); return;
        }

        if (atom.StartsWith("----:", StringComparison.Ordinal))
        {
            file.SetFreeform(AppleMean, atom[5..], values);
        }
        else if (values.Count == 0)
        {
            tag.ClearData(Atom(atom));
        }
        else
        {
            tag.SetText(Atom(atom), values.ToArray());
        }
    }

    /// <summary>
    /// File MP4 dengan akses ke box "ilst". SetDashBoxes milik TagLib (2.3) menulis nilai ganda dengan struktur yang
    /// rusak (mean/name/data diulang di satu atom, dan atomnya ditambahkan berkali-kali), sehingga Picard dan mutagen
    /// tidak bisa membacanya. Di sini atom freeform dibangun sesuai bentuk iTunes: satu atom "----" berisi satu mean,
    /// satu name, lalu satu data untuk setiap nilai.
    /// </summary>
    private sealed class Mp4File(string path) : TagLib.Mpeg4.File(path, ReadStyle.Average)
    {
        // Nama box MP4 (konstanta BoxType milik TagLib tidak publik)
        private static readonly ByteVector MetaBox = "meta", IlstBox = "ilst", DashBox = "----", MeanBox = "mean", NameBox = "name", DataBox = "data";

        public void SetFreeform(string mean, string name, IReadOnlyList<string> values)
        {
            // Box ilst yang sama dengan milik AppleTag (sudah dibuat oleh GetTag(TagTypes.Apple, true))
            var ilst = UdtaBoxes
                .Select(udta => udta.GetChild(MetaBox)?.GetChild(IlstBox))
                .FirstOrDefault(box => box is not null)
                ?? throw new InvalidOperationException("The MP4 file has no iTunes metadata list.");

            foreach (var old in ilst.Children.Where(b => b.BoxType == DashBox && Describes(b, mean, name)).ToList())
                ilst.RemoveChild(old);
            if (values.Count == 0)
                return;

            var dash = new TagLib.Mpeg4.AppleAnnotationBox(DashBox);
            dash.AddChild(new TagLib.Mpeg4.AppleAdditionalInfoBox(MeanBox, 0, 1) { Text = mean });
            dash.AddChild(new TagLib.Mpeg4.AppleAdditionalInfoBox(NameBox, 0, 1) { Text = name });
            foreach (string value in values)
                dash.AddChild(new TagLib.Mpeg4.AppleDataBox(DataBox, (uint)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsText) { Text = value });
            ilst.AddChild(dash);
        }

        private static bool Describes(TagLib.Mpeg4.Box dash, string mean, string name) =>
            dash.Children.OfType<TagLib.Mpeg4.AppleAdditionalInfoBox>().Any(b => b.BoxType == MeanBox && b.Text == mean)
            && dash.Children.OfType<TagLib.Mpeg4.AppleAdditionalInfoBox>().Any(b => b.BoxType == NameBox && b.Text == name);
    }

    // ── Bantuan ─────────────────────────────────────────────

    // Nama atom seperti "©nam": karakter © adalah byte 0xA9, jadi dibentuk lewat Latin-1
    private static ByteVector Atom(string name) => ByteVector.FromString(name, StringType.Latin1);

    private static string[] First(params string[][] candidates) =>
        candidates.FirstOrDefault(c => c.Length > 0) ?? [];

    private static string[] Number(uint value) => value > 0 ? [value.ToString(CultureInfo.InvariantCulture)] : [];

    private static uint ParseNumber(string? text) =>
        uint.TryParse(text?.Split('/')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint number) ? number : 0;

    private static bool IsTrue(string? text) =>
        text is not null && (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

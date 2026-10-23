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
/// menyimpan daftar kredit (arranger, producer, ...) dengan benar lewat TagLib.
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

        foreach (var field in TagFields.All.Where(f => f.IsSupported(format)))
        {
            var values = format switch
            {
                TagFormat.Vorbis => ReadVorbis((TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true), field),
                TagFormat.Mp4 => ReadMp4((TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true), field),
                _ => ReadId3((TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true), field),
            };
            var clean = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
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
    /// Field yang tidak didukung format file ini (misalnya Arranger di M4A) dilewati.
    /// </summary>
    public static void Write(string path, IReadOnlyDictionary<string, string> changes, IReadOnlyList<TagPicture>? pictures = null)
    {
        // Ditulis ke salinan di folder yang sama (supaya penggantian akhirnya atomik), bernama tersembunyi dan
        // berakhiran .tmp, jadi tidak ikut terbaca scan library kalau sampai tertinggal
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporary = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        System.IO.File.Copy(path, temporary);
        try
        {
            if (OperatingSystem.IsWindows())
                System.IO.File.SetAttributes(temporary, System.IO.File.GetAttributes(temporary) | FileAttributes.Hidden);

            // TagLib memilih pembaca dari ekstensi, jadi jenis file asli diberikan langsung. File MP4 dibuka lewat
            // turunan kelasnya sendiri, supaya atom freeform bisa ditulis dengan struktur yang benar (lihat Mp4File).
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            using (var file = extension is "m4a" or "mp4" or "m4b" or "aac"
                       ? new Mp4File(temporary)
                       : TagLib.File.Create(temporary, "taglib/" + extension, ReadStyle.Average))
            {
                Apply(file, changes, pictures);
                file.Save();
            }

            if (OperatingSystem.IsWindows())
                System.IO.File.SetAttributes(temporary, System.IO.File.GetAttributes(temporary) & ~FileAttributes.Hidden);
            System.IO.File.Replace(temporary, path, null);
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
                System.IO.File.Delete(temporary);
        }
    }

    public static TagFormat FormatOf(TagLib.File file) => file switch
    {
        TagLib.Flac.File or TagLib.Ogg.File => TagFormat.Vorbis,
        TagLib.Mpeg4.File => TagFormat.Mp4,
        _ => TagFormat.Id3,
    };

    private static void Apply(TagLib.File file, IReadOnlyDictionary<string, string> changes, IReadOnlyList<TagPicture>? pictures)
    {
        var format = FormatOf(file);
        if (format == TagFormat.Id3)
        {
            TagLib.Id3v2.Tag.DefaultVersion = 4;
            TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        }

        foreach (var (key, value) in changes)
        {
            var field = TagFields.Get(key);
            if (!field.IsSupported(format))
                continue;

            var values = TagFields.Split(value, field.MultiValue);
            switch (format)
            {
                case TagFormat.Vorbis:
                    WriteVorbis((TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph, true), field, values);
                    break;
                case TagFormat.Mp4:
                    WriteMp4((TagLib.Mpeg4.AppleTag)file.GetTag(TagTypes.Apple, true), (Mp4File)file, field, values);
                    break;
                default:
                    WriteId3((TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2, true), field, values);
                    break;
            }
        }

        if (pictures is not null)
        {
            file.Tag.Pictures = pictures
                .Select(p => (IPicture)new Picture(new ByteVector(p.Data)) { Type = p.Type, MimeType = p.MimeType, Description = p.Description })
                .ToArray();
        }
    }

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

    private static string[] ReadId3(TagLib.Id3v2.Tag tag, TagField field)
    {
        string id = field.Id3;
        switch (field.Key)
        {
            case "tracknumber": return Number(tag.Track);
            case "totaltracks": return Number(tag.TrackCount);
            case "discnumber": return Number(tag.Disc);
            case "totaldiscs": return Number(tag.DiscCount);
            case "comment": return [tag.Comment ?? ""];
            case "lyrics": return [tag.Lyrics ?? ""];
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

    private static void WriteId3(TagLib.Id3v2.Tag tag, TagField field, IReadOnlyList<string> values)
    {
        string id = field.Id3;
        string? single = values.Count > 0 ? values[0] : null;
        switch (field.Key)
        {
            case "tracknumber": SetNumberPair(tag, "TRCK", ParseNumber(single), null); return;
            case "totaltracks": SetNumberPair(tag, "TRCK", null, ParseNumber(single)); return;
            case "discnumber": SetNumberPair(tag, "TPOS", ParseNumber(single), null); return;
            case "totaldiscs": SetNumberPair(tag, "TPOS", null, ParseNumber(single)); return;
            case "comment": tag.Comment = single; return;
            case "lyrics": tag.Lyrics = single; return;
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
    /// TRCK dan TPOS ditulis langsung sebagai "3/12" seperti Picard; properti Track milik TagLib menulis "03/12".
    /// Hanya bagian yang diberikan yang diganti (nomor atau totalnya), bagian lainnya diambil dari frame yang ada.
    /// </summary>
    private static void SetNumberPair(TagLib.Id3v2.Tag tag, string frameId, uint? number, uint? total)
    {
        string[] current = (TextInformationFrame.Get(tag, frameId, false)?.Text.FirstOrDefault() ?? "").Split('/');
        uint n = number ?? ParseNumber(current[0]);
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

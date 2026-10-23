using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicPlayer.Library;

/// <summary>Data satu file yang dipakai untuk mencocokkannya dengan lagu di rilis MusicBrainz.</summary>
public sealed record MatchFile(string Path, int? Disc, int? Track, string Title, TimeSpan Duration);

/// <summary>Hasil pencocokan satu file. <see cref="Track"/> null kalau tidak ada lagu yang cukup cocok.</summary>
public sealed record TrackMatch(string Path, MbMedium? Medium, MbTrack? Track, int Score);

/// <summary>
/// Mengubah rilis MusicBrainz menjadi tag, dengan nama dan aturan yang sama seperti MusicBrainz Picard, dan mencocokkan
/// file-file album dengan lagu-lagu di rilis itu.
/// </summary>
public static class MusicBrainzTagger
{
    /// <summary>ID MusicBrainz untuk "Various Artists": rilis seperti ini ditandai compilation, seperti di Picard.</summary>
    public const string VariousArtistsId = "89ad4ac3-39f7-470e-963a-56509c546377";

    // Pencocokan diterima mulai skor ini: misalnya nomor track cocok, atau judul sama persis
    private const int MinimumScore = 40;

    /// <summary>
    /// Field yang khusus milik satu rilis. Kalau rilis yang dipilih tidak memilikinya, field ini dikosongkan, supaya
    /// tidak tersisa data dari rilis lain (misalnya barcode atau ID MusicBrainz yang lama).
    /// </summary>
    private static readonly string[] ReleaseSpecificKeys =
    [
        "artists", "artistsort", "albumartistsort", "originaldate", "discsubtitle", "media", "releasestatus", "releasetype",
        "releasecountry", "label", "catalognumber", "barcode", "asin", "script", "language", "isrc",
        "musicbrainz_recordingid", "musicbrainz_trackid", "musicbrainz_albumid", "musicbrainz_releasegroupid",
        "musicbrainz_artistid", "musicbrainz_albumartistid",
    ];

    /// <summary>
    /// Mencocokkan setiap file dengan paling banyak satu lagu, dan setiap lagu dengan paling banyak satu file. Petunjuknya:
    /// nomor disc dan track, durasi, dan judul. Pasangan dengan skor tertinggi dipilih lebih dulu.
    /// </summary>
    public static IReadOnlyList<TrackMatch> Match(IReadOnlyList<MatchFile> files, MbRelease release)
    {
        var candidates = new List<(int File, MbMedium Medium, MbTrack Track, int Score, double DurationGap)>();
        for (int i = 0; i < files.Count; i++)
        {
            foreach (var medium in release.Media)
            {
                foreach (var track in medium.Tracks)
                {
                    int score = Score(files[i], medium, track, out double gap);
                    if (score >= MinimumScore)
                        candidates.Add((i, medium, track, score, gap));
                }
            }
        }

        var result = new TrackMatch?[files.Count];
        var usedTracks = new HashSet<MbTrack>();
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.DurationGap))
        {
            if (result[c.File] is not null || !usedTracks.Add(c.Track))
                continue;
            result[c.File] = new TrackMatch(files[c.File].Path, c.Medium, c.Track, c.Score);
        }

        return files.Select((f, i) => result[i] ?? new TrackMatch(f.Path, null, null, 0)).ToList();
    }

    private static int Score(MatchFile file, MbMedium medium, MbTrack track, out double durationGap)
    {
        int score = 0;
        if (file.Track is { } number && number == track.Position && (file.Disc ?? 1) == medium.Position)
            score += 40;

        durationGap = double.MaxValue;
        if (track.LengthMs is { } length && file.Duration > TimeSpan.Zero)
        {
            durationGap = Math.Abs(file.Duration.TotalSeconds - length / 1000.0);
            score += durationGap switch
            {
                <= 2 => 30,
                <= 5 => 15,
                > 15 => -20, // durasi jauh berbeda: hampir pasti lagu lain (atau versi lain)
                _ => 0,
            };
        }

        string fileTitle = NormalizeTitle(file.Title), trackTitle = NormalizeTitle(track.Title);
        if (fileTitle.Length > 0 && fileTitle == trackTitle)
            score += 40;
        else if (fileTitle.Length >= 3 && trackTitle.Length >= 3 && (fileTitle.Contains(trackTitle) || trackTitle.Contains(fileTitle)))
            score += 20;

        return score;
    }

    private static readonly Regex FeaturingPart = new(
        @"[\(\[（]\s*(feat\.?|ft\.?|featuring)\s[^\)\]）]*[\)\]）]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Judul untuk dibandingkan: huruf kecil, tanpa "(feat. …)", tanpa spasi dan tanda baca.</summary>
    public static string NormalizeTitle(string title)
    {
        string text = FeaturingPart.Replace(title, "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Tag untuk satu lagu dari rilis ini, dengan kunci field Picard (lihat TagFields). Field milik rilis yang tidak
    /// tersedia diberi nilai kosong (dihapus saat disimpan); tanggal yang tidak tersedia dibiarkan apa adanya.
    /// </summary>
    /// <param name="preferEnglishNames">Pakai alias bahasa Inggris artis kalau ada (misalnya "Nightcord at 25:00"
    /// untuk "25時、ナイトコードで。"), seperti opsi "Translate artist names" di Picard.</param>
    public static Dictionary<string, string> BuildTags(MbRelease release, MbMedium medium, MbTrack track, bool preferEnglishNames)
    {
        var tags = ReleaseSpecificKeys.ToDictionary(k => k, _ => "");
        var trackCredits = track.Credits.Count > 0 ? track.Credits : release.Credits;

        tags["title"] = track.Title;
        tags["artist"] = CreditText(trackCredits, preferEnglishNames, sort: false);
        tags["artistsort"] = CreditText(trackCredits, preferEnglishNames, sort: true);
        tags["artists"] = Join(trackCredits.Select(c => Name(c, preferEnglishNames, sort: false)));
        tags["musicbrainz_artistid"] = Join(trackCredits.Select(c => c.Artist.Id));

        tags["album"] = release.Title;
        tags["albumartist"] = CreditText(release.Credits, preferEnglishNames, sort: false);
        tags["albumartistsort"] = CreditText(release.Credits, preferEnglishNames, sort: true);
        tags["musicbrainz_albumartistid"] = Join(release.Credits.Select(c => c.Artist.Id));

        if (release.Date is { } date)
            tags["date"] = date;
        tags["originaldate"] = release.FirstReleaseDate ?? "";

        // Seperti Picard: nomor track adalah urutannya di disc (bukan label seperti "A1" di piringan hitam)
        tags["tracknumber"] = track.Position.ToString(CultureInfo.InvariantCulture);
        tags["totaltracks"] = medium.TrackCount.ToString(CultureInfo.InvariantCulture);
        tags["discnumber"] = medium.Position.ToString(CultureInfo.InvariantCulture);
        tags["totaldiscs"] = release.Media.Count.ToString(CultureInfo.InvariantCulture);
        tags["discsubtitle"] = medium.Title ?? "";
        tags["media"] = medium.Format ?? "";

        tags["releasestatus"] = release.Status?.ToLowerInvariant() ?? "";
        tags["releasetype"] = Join(new[] { release.PrimaryType }.Concat(release.SecondaryTypes)
            .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!.ToLowerInvariant()));
        tags["releasecountry"] = release.Country ?? "";
        tags["label"] = Join(release.Labels.Select(l => l.Name).OfType<string>().Distinct());
        tags["catalognumber"] = Join(release.Labels.Select(l => l.CatalogNumber).OfType<string>().Distinct());
        tags["barcode"] = release.Barcode ?? "";
        tags["asin"] = release.Asin ?? "";
        tags["script"] = release.Script ?? "";
        tags["language"] = release.Language ?? "";
        tags["isrc"] = Join(track.Isrcs);

        tags["musicbrainz_albumid"] = release.Id;
        tags["musicbrainz_releasegroupid"] = release.ReleaseGroupId ?? "";
        tags["musicbrainz_trackid"] = track.Id;
        tags["musicbrainz_recordingid"] = track.RecordingId;

        if (release.Credits.Any(c => c.Artist.Id == VariousArtistsId))
            tags["compilation"] = "1";

        return tags;
    }

    /// <summary>Teks kredit utuh: nama-nama dengan joinphrase-nya, misalnya "A feat. B" atau "A × B".</summary>
    public static string CreditText(IReadOnlyList<MbCredit> credits, bool preferEnglishNames, bool sort) =>
        string.Concat(credits.Select(c => Name(c, preferEnglishNames, sort) + c.JoinPhrase)).Trim();

    private static string Name(MbCredit credit, bool preferEnglishNames, bool sort)
    {
        if (preferEnglishNames && EnglishAlias(credit.Artist) is { } alias)
            return sort ? (alias.SortName.Length > 0 ? alias.SortName : alias.Name) : alias.Name;

        // Nama sesuai yang tercetak di rilis; urutan sortir dari nama resmi artisnya
        return sort ? (credit.Artist.SortName.Length > 0 ? credit.Artist.SortName : credit.Name) : credit.Name;
    }

    /// <summary>Alias bahasa Inggris: yang ditandai primary lebih dulu, seperti Picard.</summary>
    private static MbAlias? EnglishAlias(MbArtist artist) =>
        artist.Aliases
            .Where(a => a.Locale is { } locale && (locale == "en" || locale.StartsWith("en_", StringComparison.Ordinal)) && a.Name.Length > 0)
            .OrderByDescending(a => a.Primary)
            .FirstOrDefault();

    private static string Join(IEnumerable<string?> values) =>
        string.Join(TagFields.Separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>Judul album untuk mencari di MusicBrainz: tanpa akhiran " - Single"/" - EP" gaya Apple Music.</summary>
    public static string SearchTitle(string album) =>
        Regex.Replace(album, @"\s+-\s+(Single|EP)\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
}

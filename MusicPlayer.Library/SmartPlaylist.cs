using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MusicPlayer.Library;

/// <summary>Data lagu yang bisa dipakai aturan smart playlist.</summary>
public enum SmartField
{
    Title,
    Artist,
    AlbumArtist,
    Album,
    Genre,
    Year,
    Folder,
    Format,
    Quality,
    SampleRate,
    BitDepth,
    Duration,
    DateAdded,
    PlayCount,
    LastPlayed,
    Favourite,
    HasLyrics,
}

/// <summary>Jenis nilai sebuah <see cref="SmartField"/>, yang menentukan operator dan isian nilainya.</summary>
public enum SmartFieldKind
{
    /// <summary>Teks; tanpa membedakan huruf besar kecil, hiragana dan katakana, serta huruf lebar dan sempit.</summary>
    Text,

    /// <summary>Angka dengan satuan yang dilihat pengguna: tahun, kHz, bit, menit, jumlah putar.</summary>
    Number,

    /// <summary>Tanggal, dibandingkan dengan "dalam N hari terakhir".</summary>
    Date,

    /// <summary>Hi-Res, Lossless, atau Lossy (lihat <see cref="AudioQuality"/>).</summary>
    Quality,

    /// <summary>Ya atau tidak, tanpa isian nilai.</summary>
    Boolean,

    /// <summary>Path folder; lagu di subfolder-nya ikut.</summary>
    Folder,
}

public enum SmartOperator
{
    Contains,
    DoesNotContain,
    Is,
    IsNot,
    StartsWith,
    EndsWith,
    GreaterThan,
    LessThan,
    InTheLast,
    NotInTheLast,
}

/// <summary>Urutan lagu di smart playlist; dengan batas jumlah lagu, lagu yang diambil juga menurut urutan ini.</summary>
public enum SmartOrder
{
    /// <summary>Per album: artis album, album, disc, nomor track.</summary>
    Album,
    Title,
    Artist,
    RecentlyAdded,
    MostPlayed,
    RecentlyPlayed,
    Random,
}

/// <summary>Satu aturan, misalnya Artist · contains · "ヨルシカ". Nilai angka memakai titik desimal.</summary>
public sealed record SmartRule(SmartField Field, SmartOperator Operator, string Value = "");

/// <summary>
/// Isi smart playlist: lagu di library yang cocok dengan semua aturan (<paramref name="MatchAll"/>) atau salah satunya,
/// diurutkan menurut <paramref name="Order"/>, paling banyak <paramref name="Limit"/> lagu. Tanpa aturan, semua lagu cocok
/// (misalnya "25 lagu yang paling sering diputar"). Isinya selalu dihitung ulang, jadi ikut berubah bersama library.
/// </summary>
public sealed record SmartPlaylistRules(bool MatchAll, IReadOnlyList<SmartRule> Rules, SmartOrder Order = SmartOrder.Album, int? Limit = null);

/// <summary>Jumlah putar dan waktu terakhir diputar sebuah lagu.</summary>
public readonly record struct PlayStats(int Count, DateTime LastPlayedUtc);

/// <summary>Data di luar tag lagu yang dipakai aturan: riwayat putar dan favorit per path, waktu sekarang, dan pengacak.</summary>
public sealed record SmartPlaylistContext(
    IReadOnlyDictionary<string, PlayStats> Plays, IReadOnlySet<string> Favourites, DateTime NowUtc, Random Random);

/// <summary>Aturan smart playlist: operator yang berlaku untuk setiap data, pencocokan lagu, dan format penyimpanannya.</summary>
public static class SmartPlaylists
{
    private const CompareOptions TextOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    private static readonly SmartOperator[] TextOperators =
        [SmartOperator.Contains, SmartOperator.DoesNotContain, SmartOperator.Is, SmartOperator.IsNot, SmartOperator.StartsWith, SmartOperator.EndsWith];
    private static readonly SmartOperator[] NumberOperators = [SmartOperator.Is, SmartOperator.IsNot, SmartOperator.GreaterThan, SmartOperator.LessThan];
    private static readonly SmartOperator[] RangeOperators = [SmartOperator.GreaterThan, SmartOperator.LessThan];
    private static readonly SmartOperator[] DateOperators = [SmartOperator.InTheLast, SmartOperator.NotInTheLast];
    private static readonly SmartOperator[] ChoiceOperators = [SmartOperator.Is, SmartOperator.IsNot];

    public static SmartFieldKind KindOf(SmartField field) => field switch
    {
        SmartField.Year or SmartField.SampleRate or SmartField.BitDepth or SmartField.Duration or SmartField.PlayCount => SmartFieldKind.Number,
        SmartField.DateAdded or SmartField.LastPlayed => SmartFieldKind.Date,
        SmartField.Quality => SmartFieldKind.Quality,
        SmartField.Favourite or SmartField.HasLyrics => SmartFieldKind.Boolean,
        SmartField.Folder => SmartFieldKind.Folder,
        _ => SmartFieldKind.Text,
    };

    /// <summary>Operator yang bisa dipilih untuk data ini, yang pertama sebagai bawaan.</summary>
    public static IReadOnlyList<SmartOperator> OperatorsFor(SmartField field) => field switch
    {
        SmartField.Duration => RangeOperators, // durasi yang persis sama hampir tidak pernah dicari
        _ => KindOf(field) switch
        {
            SmartFieldKind.Number => NumberOperators,
            SmartFieldKind.Date => DateOperators,
            SmartFieldKind.Text => TextOperators,
            _ => ChoiceOperators,
        },
    };

    /// <summary>
    /// Aturan ini lengkap: operatornya berlaku untuk datanya, dan nilainya terisi (angka untuk angka dan tanggal, salah satu
    /// golongan kualitas, folder). Aturan teks boleh kosong ("Genre is" kosong: lagu tanpa genre).
    /// </summary>
    public static bool IsComplete(SmartRule rule) =>
        OperatorsFor(rule.Field).Contains(rule.Operator) && KindOf(rule.Field) switch
        {
            SmartFieldKind.Number or SmartFieldKind.Date => TryNumber(rule.Value, out double n) && n >= 0,
            SmartFieldKind.Quality => TryQuality(rule.Value, out _),
            SmartFieldKind.Folder => rule.Value.Trim().Length > 0,
            _ => true,
        };

    /// <summary>Lagu yang cocok, sudah diurutkan dan dibatasi jumlahnya.</summary>
    public static IReadOnlyList<Track> Evaluate(SmartPlaylistRules rules, IEnumerable<Track> tracks, SmartPlaylistContext context)
    {
        var matchers = rules.Rules.Select(rule => Matcher(rule, context)).ToList();
        var matching = tracks.Where(track =>
            matchers.Count == 0 || (rules.MatchAll ? matchers.All(m => m(track)) : matchers.Any(m => m(track)))).ToList();

        var ordered = Ordered(matching, rules.Order, context);
        return rules.Limit is > 0 and int limit ? ordered.Take(limit).ToList() : ordered;
    }

    /// <summary>Satu aturan cocok dengan lagu ini. Aturan yang belum lengkap tidak cocok dengan lagu mana pun.</summary>
    public static bool Matches(SmartRule rule, Track track, SmartPlaylistContext context) => Matcher(rule, context)(track);

    // Nilai aturan dibaca sekali per aturan, bukan sekali per lagu
    private static Func<Track, bool> Matcher(SmartRule rule, SmartPlaylistContext context)
    {
        if (!IsComplete(rule))
            return _ => false;

        bool not = rule.Operator is SmartOperator.IsNot or SmartOperator.DoesNotContain or SmartOperator.NotInTheLast;
        switch (KindOf(rule.Field))
        {
            case SmartFieldKind.Text:
            {
                string value = rule.Value.Trim();
                return track => MatchesText(TextOf(rule.Field, track), rule.Operator, value);
            }
            case SmartFieldKind.Number:
            {
                TryNumber(rule.Value, out double value);
                double tolerance = rule.Field == SmartField.SampleRate ? 0.05 : 1e-9; // 44.1 kHz = 44100 Hz
                return track => NumberOf(rule.Field, track, context) is not double number
                    ? not // lagu tanpa tahun: "Year is not 2020" cocok, "Year is greater than 2000" tidak
                    : rule.Operator switch
                    {
                        SmartOperator.Is => Math.Abs(number - value) < tolerance,
                        SmartOperator.IsNot => Math.Abs(number - value) >= tolerance,
                        SmartOperator.GreaterThan => number > value + tolerance,
                        _ => number < value - tolerance,
                    };
            }
            case SmartFieldKind.Date:
            {
                TryNumber(rule.Value, out double days);
                var since = context.NowUtc - TimeSpan.FromDays(days);
                // Lagu yang belum pernah diputar tidak "diputar dalam 30 hari terakhir", jadi cocok dengan kebalikannya
                return track => (DateOf(rule.Field, track, context) is DateTime date && date >= since) != not;
            }
            case SmartFieldKind.Quality:
            {
                TryQuality(rule.Value, out var quality);
                return track => (AudioQuality.Describe(track).Kind == quality) != not;
            }
            case SmartFieldKind.Folder:
            {
                string folder = LibraryPaths.NormalizeFolder(rule.Value);
                return track => LibraryPaths.IsUnder(track.FilePath, folder) != not;
            }
            default:
            {
                return track => (rule.Field == SmartField.Favourite
                    ? context.Favourites.Contains(track.FilePath)
                    : track.HasLyrics) != not;
            }
        }
    }

    private static string TextOf(SmartField field, Track track) => field switch
    {
        SmartField.Title => track.DisplayTitle,
        SmartField.Artist => track.Artist ?? track.AlbumArtist ?? "",
        SmartField.AlbumArtist => track.AlbumArtist ?? track.Artist ?? "",
        SmartField.Album => track.Album ?? "",
        SmartField.Genre => track.Genre ?? "",
        _ => track.Codec,
    };

    private static bool MatchesText(string text, SmartOperator op, string value) => op switch
    {
        SmartOperator.Contains => Compare.IndexOf(text, value, TextOptions) >= 0,
        SmartOperator.DoesNotContain => Compare.IndexOf(text, value, TextOptions) < 0,
        SmartOperator.Is => Compare.Compare(text, value, TextOptions) == 0,
        SmartOperator.IsNot => Compare.Compare(text, value, TextOptions) != 0,
        SmartOperator.StartsWith => Compare.IsPrefix(text, value, TextOptions),
        _ => Compare.IsSuffix(text, value, TextOptions),
    };

    private static double? NumberOf(SmartField field, Track track, SmartPlaylistContext context) => field switch
    {
        SmartField.Year => track.Year,
        SmartField.SampleRate => track.SampleRate > 0 ? track.SampleRate / 1000.0 : null,
        SmartField.BitDepth => track.BitsPerSample > 0 ? track.BitsPerSample : null,
        SmartField.Duration => track.Duration.TotalMinutes,
        _ => context.Plays.TryGetValue(track.FilePath, out var plays) ? plays.Count : 0,
    };

    private static DateTime? DateOf(SmartField field, Track track, SmartPlaylistContext context) => field == SmartField.DateAdded
        ? new DateTime(track.AddedUtcTicks, DateTimeKind.Utc)
        : context.Plays.TryGetValue(track.FilePath, out var plays) ? plays.LastPlayedUtc : null;

    private static List<Track> Ordered(List<Track> tracks, SmartOrder order, SmartPlaylistContext context)
    {
        if (order == SmartOrder.Random)
        {
            var shuffled = tracks.ToList();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = context.Random.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            return shuffled;
        }

        PlayStats Stats(Track t) => context.Plays.TryGetValue(t.FilePath, out var s) ? s : default;
        var byAlbum = Comparer<Track>.Create(CompareAlbumOrder);
        IOrderedEnumerable<Track> sorted = order switch
        {
            SmartOrder.Title => tracks.OrderBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(t => t, byAlbum),
            SmartOrder.Artist => tracks.OrderBy(t => t.Artist ?? t.AlbumArtist ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(t => t, byAlbum),
            SmartOrder.RecentlyAdded => tracks.OrderByDescending(t => t.AddedUtcTicks).ThenBy(t => t, byAlbum),
            SmartOrder.MostPlayed => tracks.OrderByDescending(t => Stats(t).Count).ThenByDescending(t => Stats(t).LastPlayedUtc).ThenBy(t => t, byAlbum),
            SmartOrder.RecentlyPlayed => tracks.OrderByDescending(t => Stats(t).LastPlayedUtc).ThenBy(t => t, byAlbum),
            _ => tracks.OrderBy(t => t, byAlbum),
        };
        return sorted.ToList();
    }

    // Sama seperti urutan album di database: artis album, album, disc (kosong = 1), nomor track (kosong di akhir), path
    private static int CompareAlbumOrder(Track a, Track b)
    {
        int c = string.Compare(a.AlbumArtist ?? a.Artist, b.AlbumArtist ?? b.Artist, StringComparison.OrdinalIgnoreCase);
        if (c == 0) c = string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase);
        if (c == 0) c = (a.DiscNumber ?? 1).CompareTo(b.DiscNumber ?? 1);
        if (c == 0) c = (a.TrackNumber ?? int.MaxValue).CompareTo(b.TrackNumber ?? int.MaxValue);
        if (c == 0) c = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
        return c;
    }

    private static bool TryNumber(string value, out double number) =>
        double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static bool TryQuality(string value, out QualityKind quality) => TryName(QualityNames, value, out quality);

    // ── Penyimpanan ─────────────────────────────────────────

    // Nama di JSON ditulis sendiri, terpisah dari nama di kode: format tersimpan tidak berubah kalau kode di-rename, dan
    // tetap jalan saat aplikasi di-publish dengan trimming (tanpa Enum.Parse)
    private const int Version = 1;

    private static readonly (SmartField Value, string Name)[] FieldNames =
    [
        (SmartField.Title, "title"), (SmartField.Artist, "artist"), (SmartField.AlbumArtist, "albumArtist"),
        (SmartField.Album, "album"), (SmartField.Genre, "genre"), (SmartField.Year, "year"), (SmartField.Folder, "folder"),
        (SmartField.Format, "format"), (SmartField.Quality, "quality"), (SmartField.SampleRate, "sampleRate"),
        (SmartField.BitDepth, "bitDepth"), (SmartField.Duration, "duration"), (SmartField.DateAdded, "dateAdded"),
        (SmartField.PlayCount, "playCount"), (SmartField.LastPlayed, "lastPlayed"), (SmartField.Favourite, "favourite"),
        (SmartField.HasLyrics, "hasLyrics"),
    ];

    private static readonly (SmartOperator Value, string Name)[] OperatorNames =
    [
        (SmartOperator.Contains, "contains"), (SmartOperator.DoesNotContain, "doesNotContain"), (SmartOperator.Is, "is"),
        (SmartOperator.IsNot, "isNot"), (SmartOperator.StartsWith, "startsWith"), (SmartOperator.EndsWith, "endsWith"),
        (SmartOperator.GreaterThan, "greaterThan"), (SmartOperator.LessThan, "lessThan"),
        (SmartOperator.InTheLast, "inTheLast"), (SmartOperator.NotInTheLast, "notInTheLast"),
    ];

    private static readonly (SmartOrder Value, string Name)[] OrderNames =
    [
        (SmartOrder.Album, "album"), (SmartOrder.Title, "title"), (SmartOrder.Artist, "artist"),
        (SmartOrder.RecentlyAdded, "recentlyAdded"), (SmartOrder.MostPlayed, "mostPlayed"),
        (SmartOrder.RecentlyPlayed, "recentlyPlayed"), (SmartOrder.Random, "random"),
    ];

    /// <summary>Nama golongan kualitas yang disimpan sebagai nilai aturan Quality.</summary>
    public static readonly (QualityKind Value, string Name)[] QualityNames =
        [(QualityKind.HiRes, "hiRes"), (QualityKind.Lossless, "lossless"), (QualityKind.Lossy, "lossy")];

    private static bool TryName<T>((T Value, string Name)[] names, string? name, out T value)
    {
        foreach (var entry in names)
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    private static string NameOf<T>((T Value, string Name)[] names, T value) where T : struct, Enum =>
        names.First(entry => EqualityComparer<T>.Default.Equals(entry.Value, value)).Name;

    public static string ToJson(SmartPlaylistRules rules)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("match", rules.MatchAll ? "all" : "any");
            writer.WriteString("order", NameOf(OrderNames, rules.Order));
            if (rules.Limit is int limit)
                writer.WriteNumber("limit", limit);
            writer.WriteStartArray("rules");
            foreach (var rule in rules.Rules)
            {
                writer.WriteStartObject();
                writer.WriteString("field", NameOf(FieldNames, rule.Field));
                writer.WriteString("operator", NameOf(OperatorNames, rule.Operator));
                writer.WriteString("value", rule.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Null kalau teksnya bukan aturan smart playlist. Aturan dengan data atau operator yang tidak dikenal (dari versi aplikasi
    /// yang lebih baru) dilewati.
    /// </summary>
    public static SmartPlaylistRules? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rules", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;

            var rules = new List<SmartRule>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && TryName(FieldNames, Text(item, "field"), out SmartField field)
                    && TryName(OperatorNames, Text(item, "operator"), out SmartOperator op))
                    rules.Add(new SmartRule(field, op, Text(item, "value") ?? ""));
            }

            bool matchAll = !string.Equals(Text(root, "match"), "any", StringComparison.OrdinalIgnoreCase);
            var order = TryName(OrderNames, Text(root, "order"), out SmartOrder o) ? o : SmartOrder.Album;
            int? limit = root.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out int n) && n > 0
                ? n
                : null;
            return new SmartPlaylistRules(matchAll, rules, order, limit);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

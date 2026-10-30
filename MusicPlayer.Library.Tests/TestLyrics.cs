namespace MusicPlayer.Library.Tests;

/// <summary>Pembuat lirik untuk tes, supaya setiap tes cukup menulis isi barisnya.</summary>
internal static class TestLyrics
{
    /// <summary>Waktu dalam detik, dibulatkan ke milidetik supaya sama persis dengan waktu hasil LrcParser.</summary>
    public static TimeSpan At(double seconds) => TimeSpan.FromMilliseconds((long)Math.Round(seconds * 1000));

    public static LyricLine Line(double seconds, string text) => new(At(seconds), text);

    public static Lyrics Synced(params LyricLine[] lines) => new(lines, isSynced: true);

    public static Lyrics Synced(params (double Seconds, string Text)[] lines) =>
        new(lines.Select(l => Line(l.Seconds, l.Text)).ToList(), isSynced: true);

    public static Lyrics Plain(params string[] texts) =>
        new(texts.Select(t => new LyricLine(TimeSpan.Zero, t)).ToList(), isSynced: false);

    /// <summary>Lirik tanpa tanda waktu beserta cara baca dan terjemahannya, seperti lirik di tag lagu.</summary>
    public static Lyrics Tagged(params (string Text, string? Reading, string? Translation)[] lines) =>
        new(lines.Select(l => new LyricLine(TimeSpan.Zero, l.Text) { Reading = l.Reading, Translation = l.Translation })
            .ToList(), isSynced: false);

    public static string[] Texts(Lyrics lyrics) => [.. lyrics.Lines.Select(l => l.Text)];

    public static TimeSpan[] Times(Lyrics lyrics) => [.. lyrics.Lines.Select(l => l.Time)];

    public static string?[] Readings(Lyrics lyrics) => [.. lyrics.Lines.Select(l => l.Reading)];

    public static string?[] Translations(Lyrics lyrics) => [.. lyrics.Lines.Select(l => l.Translation)];
}

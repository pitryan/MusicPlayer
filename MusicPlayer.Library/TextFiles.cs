using System.Text;

namespace MusicPlayer.Library;

/// <summary>Membaca file teks kecil yang encoding-nya tidak diketahui, misalnya file .lrc.</summary>
public static class TextFiles
{
    // File lirik tidak pernah sebesar ini; file yang lebih besar hampir pasti bukan lirik
    private const long MaxBytes = 1024 * 1024;

    private const int ShiftJis = 932;
    private const int Gb18030 = 54936;
    private const int Windows1252 = 1252;

    static TextFiles()
    {
        // Encoding lama seperti Shift-JIS tidak tersedia di .NET tanpa provider ini
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Isi file sebagai teks, atau null kalau file tidak ada, terlalu besar, atau tidak bisa dibaca.</summary>
    public static string? ReadSmallText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxBytes ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// UTF-8 dan UTF-16 yang diawali BOM dikenali pasti. Tanpa BOM dicoba berurutan: UTF-8 (paling umum sekarang),
    /// Shift-JIS kalau hasilnya memuat kana (lirik Jepang lama), GB18030 (lirik Mandarin lama), lalu Windows-1252.
    /// Urutan ini sengaja: teks yang valid sebagai UTF-8 hampir pasti memang UTF-8, sedangkan GB18030 bisa "membaca"
    /// hampir semua byte, jadi diletakkan paling akhir sebelum cadangan terakhir.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes is [0xFF, 0xFE, ..])
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xFE, 0xFF, ..])
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        if (TryDecode(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), bytes) is { } utf8)
            return utf8;
        if (TryDecode(Strict(ShiftJis), bytes) is { } shiftJis && shiftJis.Any(IsKana))
            return shiftJis;
        if (TryDecode(Strict(Gb18030), bytes) is { } chinese)
            return chinese;

        return Encoding.GetEncoding(Windows1252).GetString(bytes);
    }

    private static Encoding Strict(int codePage) =>
        Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static string? TryDecode(Encoding encoding, byte[] bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // Hiragana dan katakana: tanda paling pasti bahwa teksnya bahasa Jepang
    private static bool IsKana(char c) => c is >= '\u3040' and <= '\u30FF';
}

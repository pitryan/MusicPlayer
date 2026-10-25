using System;

namespace MusicPlayer.Helpers;

/// <summary>
/// Lapisan lirik yang ditampilkan (cara baca, terjemahan): satu pengaturan untuk panel lirik di jendela utama, layar
/// penuh, dan MiniPlayer, diingat setelah aplikasi ditutup. Changed memberi tahu semua panel lirik untuk menggambar ulang.
/// </summary>
public static class LyricsDisplay
{
    private const string ReadingSetting = "LyricsShowReading";
    private const string TranslationSetting = "LyricsShowTranslation";

    public static event EventHandler? Changed;

    public static bool ShowReading
    {
        get => AppSettings.Get(ReadingSetting, true);
        set
        {
            AppSettings.Set(ReadingSetting, value);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool ShowTranslation
    {
        get => AppSettings.Get(TranslationSetting, true);
        set
        {
            AppSettings.Set(TranslationSetting, value);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}

using Windows.Foundation.Collections;
using Windows.Storage;

namespace MusicPlayer.Helpers;

/// <summary>
/// Pengaturan kecil yang diingat walaupun aplikasi ditutup (volume, shuffle, repeat).
/// Disimpan di LocalSettings milik aplikasi packaged ini.
/// </summary>
public static class AppSettings
{
    private static IPropertySet Values => ApplicationData.Current.LocalSettings.Values;

    public static T Get<T>(string key, T fallback) =>
        Values.TryGetValue(key, out object? value) && value is T typed ? typed : fallback;

    public static void Set<T>(string key, T value) where T : notnull => Values[key] = value;
}

using System;

namespace MusicPlayer.Helpers;

/// <summary>Keterangan aplikasi yang dipakai bersama halaman Settings dan log crash.</summary>
public static class AppInfo
{
    /// <summary>Versi aplikasi, misalnya "1.0.0".</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception)
        {
            // Package.Current melempar kalau aplikasi berjalan tanpa identitas package (mis. unpackaged saat debug)
            Version? version = typeof(App).Assembly.GetName().Version;
            return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}

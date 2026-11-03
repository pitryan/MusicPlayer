using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace MusicPlayer.Helpers;

/// <summary>
/// Mencatat error yang tidak tertangani ke crash.log di folder data aplikasi, supaya penyebab crash masih bisa dicari
/// setelah aplikasinya tertutup. Hanya mencatat: tidak ada error yang ditelan, jadi aplikasi tetap tertutup seperti biasa.
/// </summary>
public static class CrashLog
{
    // Lebih dari ini, log lama dipindah ke crash.old.log, supaya file-nya tidak tumbuh tanpa batas
    private const long MaxBytes = 1024 * 1024;

    private static readonly object WriteLock = new();
    private static Exception? _lastWritten;

    /// <summary>Folder "Logs" di samping database, atau di folder Temp kalau aplikasi berjalan tanpa package.</summary>
    public static string Folder { get; } = FindFolder();

    public static string FilePath => Path.Combine(Folder, "crash.log");

    /// <summary>Dipasang sekali, sedini mungkin, dari konstruktor App.</summary>
    public static void Install(Application app)
    {
        // Error di UI thread, termasuk dari event handler async void. Pesan dari WinUI kadang lebih jelas dari
        // exception-nya, yang bisa berupa COMException umum.
        app.UnhandledException += (_, e) => Write("Unhandled exception (UI thread)", e.Exception, e.Message);

        // Error di thread lain, misalnya thread audio atau Task.Run. Aplikasi selalu tertutup setelah ini.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unhandled exception", e.ExceptionObject as Exception,
                e.ExceptionObject is Exception ? null : e.ExceptionObject?.ToString());

        // Task yang gagal tanpa pernah di-await. Aplikasi tetap jalan, tapi biasanya ini tanda ada bug.
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("Unobserved task exception (the app kept running)", e.Exception, null);
    }

    private static void Write(string kind, Exception? exception, string? message)
    {
        try
        {
            lock (WriteLock)
            {
                // Error di UI thread bisa dilaporkan dua kali (UnhandledException, lalu AppDomain); cukup dicatat sekali
                if (exception is not null && ReferenceEquals(exception, _lastWritten))
                    return;
                _lastWritten = exception;

                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(file.FullName, Path.Combine(Folder, "crash.old.log"), overwrite: true);

                var text = new StringBuilder()
                    .AppendLine($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · {kind}")
                    .AppendLine($"MusicPlayer {AppInfo.Version} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}");
                if (!string.IsNullOrEmpty(message) && message != exception?.Message)
                    text.AppendLine(message);
                text.AppendLine(exception?.ToString() ?? "(no exception details)").AppendLine();

                File.AppendAllText(FilePath, text.ToString());
            }
        }
        catch (Exception)
        {
            // Menulis log tidak boleh menimbulkan error baru di tengah crash
        }
    }

    private static string FindFolder()
    {
        try
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs");
        }
        catch (Exception)
        {
            // ApplicationData melempar kalau aplikasi berjalan tanpa identitas package
            return Path.Combine(Path.GetTempPath(), "MusicPlayer", "Logs");
        }
    }
}

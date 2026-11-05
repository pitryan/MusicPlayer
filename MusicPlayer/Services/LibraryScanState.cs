using System;
using System.Threading.Tasks;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Scan library yang sedang berjalan. Disimpan di luar SettingsPage karena halaman itu dibuat baru setiap kali dibuka:
/// halaman baru tetap tahu ada scan yang berjalan, jadi tidak memulai scan kedua atau mengubah daftar folder di
/// tengahnya, dan bisa menampilkan kemajuannya. Hanya dipakai dari UI thread; <see cref="Changed"/> juga dipicu di sana.
/// </summary>
public static class LibraryScanState
{
    private static Task? _running;

    /// <summary>Scan dimulai, kemajuannya berubah, atau selesai.</summary>
    public static event EventHandler? Changed;

    public static bool IsRunning { get; private set; }

    /// <summary>Kemajuan scan yang sedang berjalan, lalu hasilnya setelah selesai.</summary>
    public static string Status { get; private set; } = "";

    /// <summary>
    /// Memulai scan, atau mengembalikan scan yang sedang berjalan. Tidak pernah gagal: kesalahannya ditaruh di
    /// <see cref="Status"/>.
    /// </summary>
    public static Task ScanAsync(LibraryDatabase database)
    {
        // IsRunning, bukan _running, yang menandai scan aktif: RunAsync sudah mengisinya sebelum await pertama
        if (!IsRunning)
            _running = RunAsync(database);
        return _running!;
    }

    private static async Task RunAsync(LibraryDatabase database)
    {
        IsRunning = true;
        SetStatus("");

        // Progress<T> yang dibuat di UI thread otomatis melapor kembali ke UI thread
        var progress = new Progress<ScanProgress>(p =>
            SetStatus(p.Total > 0 ? $"{p.StageText}... {p.Done}/{p.Total}" : $"{p.StageText}..."));

        string message;
        try
        {
            var scanner = new LibraryScanner(database);
            var result = await Task.Run(() => scanner.Scan(progress));

            message =
                $"Done in {result.Elapsed.TotalSeconds:0.0} s: {result.Added} added, {result.Updated} updated, " +
                $"{result.Removed} removed, {result.Unchanged} unchanged.";
            if (result.Errors.Count > 0)
                message += $" {result.Errors.Count} files had unreadable tags.";
            if (result.UnavailableFolders.Count > 0)
                message += $" {result.UnavailableFolders.Count} folders were unavailable and skipped.";
        }
        catch (Exception ex)
        {
            message = $"Scan failed: {ex.Message}";
        }

        IsRunning = false;
        SetStatus(message);
    }

    private static void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

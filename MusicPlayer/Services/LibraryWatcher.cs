using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using MusicPlayer.Library;

namespace MusicPlayer.Services;

/// <summary>
/// Scan otomatis: folder library dipantau dengan FileSystemWatcher, dan library di-scan beberapa detik setelah ada lagu
/// yang ditambah, diubah, dihapus, atau dipindah. Scan-nya hanya membaca file yang berubah, jadi ringan. Library juga
/// di-scan sekali sesaat setelah aplikasi dibuka, untuk perubahan selama aplikasi tertutup. Folder di drive yang sedang
/// tidak tersambung tidak bisa dipantau; untuk itu, dan untuk drive jaringan yang pemberitahuannya kurang bisa
/// diandalkan, tombol Rescan library tetap ada. Hanya dipakai dari UI thread.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    // Scan menunggu sampai tidak ada perubahan selama ini, supaya file yang masih disalin tidak dibaca setengah jadi, dan
    // satu album yang disalin cukup di-scan sekali
    private static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(3);

    // Scan pertama setelah aplikasi dibuka, sesudah jendela dan sesi terakhir selesai dimuat
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    private readonly LibraryDatabase _database;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _scanTimer;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    // Ada perubahan selama scan berjalan: scan itu sudah membaca daftar file di awal, jadi perlu di-scan lagi sesudahnya
    private bool _scanAgain;

    public LibraryWatcher(LibraryDatabase database, DispatcherQueue dispatcher)
    {
        _database = database;
        _dispatcher = dispatcher;

        _scanTimer = dispatcher.CreateTimer();
        _scanTimer.IsRepeating = false;
        _scanTimer.Tick += (_, _) => ScanNow();

        LibraryScanState.Changed += (_, _) =>
        {
            if (LibraryScanState.IsRunning)
                return;

            // Folder yang tadi tidak tersedia (drive dicolok lagi, lalu Rescan library ditekan) mulai dipantau
            SyncFolders();
            if (_scanAgain)
            {
                _scanAgain = false;
                Schedule(QuietTime);
            }
        };
    }

    /// <summary>Mulai memantau folder library, dan menjadwalkan scan pertama. Dipanggil sekali saat aplikasi dibuka.</summary>
    public void Start()
    {
        SyncFolders();
        Schedule(StartupDelay);
    }

    /// <summary>
    /// Menyesuaikan pemantauan dengan daftar folder library, setelah folder ditambah, dihapus, atau dipindah. Folder yang
    /// sudah dipantau tidak diganggu, jadi tidak ada perubahan yang terlewat di tengahnya.
    /// </summary>
    public void SyncFolders()
    {
        var folders = _database.GetFolders().Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string gone in _watchers.Keys.Where(f => !folders.Contains(f)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }

        foreach (string folder in folders.Where(f => !_watchers.ContainsKey(f)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024, // maksimum; satu album yang disalin memicu banyak pemberitahuan
                };
                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers[folder] = watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Folder ini tidak bisa dipantau (misalnya drive jaringan tertentu): tetap bisa di-scan manual
            }
        }
    }

    public void Dispose()
    {
        _scanTimer.Stop();
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
    }

    // Tiga handler di bawah dipanggil FileSystemWatcher di thread lain

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (AudioFiles.MayAffectLibrary(e.ChangeType, e.FullPath))
            RequestScan();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (AudioFiles.MayAffectLibrary(e.ChangeType, e.FullPath, e.OldFullPath))
            RequestScan();
    }

    // Terlalu banyak perubahan sekaligus untuk buffer-nya: sebagian pemberitahuan hilang, jadi scan saja
    private void OnError(object sender, ErrorEventArgs e) => RequestScan();

    private void RequestScan() => _dispatcher.TryEnqueue(() => Schedule(QuietTime));

    // Dihitung ulang setiap kali ada perubahan baru
    private void Schedule(TimeSpan delay)
    {
        _scanTimer.Stop();
        _scanTimer.Interval = delay;
        _scanTimer.Start();
    }

    private void ScanNow()
    {
        if (LibraryScanState.IsRunning)
        {
            _scanAgain = true;
            return;
        }
        _ = LibraryScanState.ScanAsync(_database); // tidak pernah gagal: kesalahannya ditampilkan di Settings
    }
}

using System.Text;
using MusicPlayer.Library;

// UTF-16 supaya path dan judul berhuruf Jepang/Korea/Mandarin tampil dengan benar
Console.InputEncoding = Encoding.Unicode;
Console.OutputEncoding = Encoding.Unicode;
Console.Title = "MusicPlayer LibraryLab";

string dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer", "library.db");
var db = new LibraryDatabase(dbPath);
var scanner = new LibraryScanner(db);
Console.WriteLine($"Database: {db.DatabasePath}");

while (true)
{
    Console.WriteLine();
    ShowFolders();
    Console.WriteLine();
    Console.WriteLine("[1] Tambah folder   [2] Hapus folder   [3] Scan ulang");
    Console.WriteLine("[4] Lihat lagu      [5] Cari lagu      [6] Statistik    [0] Keluar");
    Console.Write("Pilih: ");

    switch (Console.ReadLine()?.Trim())
    {
        case "1": AddFolder(); break;
        case "2": RemoveFolder(); break;
        case "3": RunScan(); break;
        case "4": BrowseFolder(); break;
        case "5": SearchTracks(); break;
        case "6": ShowStats(); break;
        case "0": return;
    }
}

void ShowFolders()
{
    var folders = db.GetFolders();
    Console.WriteLine("Folder library:");
    if (folders.Count == 0)
        Console.WriteLine("  (belum ada, pilih [1] untuk menambahkan)");
    for (int i = 0; i < folders.Count; i++)
        Console.WriteLine($"  [{i + 1}] {folders[i]}  ({db.CountTracks(folders[i])} lagu)");
}

void AddFolder()
{
    Console.Write("Seret folder musik ke jendela ini, lalu tekan Enter: ");
    var result = db.AddFolder(ReadPath());
    switch (result.Status)
    {
        case AddFolderStatus.NotFound:
            Console.WriteLine($"Folder tidak ditemukan: {result.Folder}");
            break;
        case AddFolderStatus.AlreadyCovered:
            Console.WriteLine($"Folder ini sudah termasuk di dalam {result.Folder}");
            break;
        case AddFolderStatus.Added:
            Console.WriteLine($"Ditambahkan: {result.Folder}");
            RunScan();
            break;
    }
}

void RemoveFolder()
{
    var folders = db.GetFolders();
    if (folders.Count == 0) return;

    Console.Write("Nomor folder yang mau dihapus dari library (file aslinya tidak ikut terhapus): ");
    if (int.TryParse(Console.ReadLine(), out int n) && n >= 1 && n <= folders.Count)
    {
        int removed = db.RemoveFolder(folders[n - 1]);
        Console.WriteLine($"Folder dihapus dari library, {removed} lagu ikut hilang dari daftar.");
    }
}

void RunScan()
{
    var consoleLock = new object();
    var progress = new InlineProgress<ScanProgress>(p =>
    {
        lock (consoleLock)
        {
            string count = p.Total > 0 ? $"{p.Done}/{p.Total}" : p.Done > 0 ? $"{p.Done}" : "";
            Console.Write($"\r{$"{p.Stage}... {count}",-50}");
        }
    });

    var result = scanner.Scan(progress);
    Console.WriteLine();
    Console.WriteLine(
        $"Selesai dalam {result.Elapsed.TotalSeconds:0.0} detik: {result.Added} baru, {result.Updated} diperbarui, " +
        $"{result.Removed} dihapus, {result.Unchanged} tidak berubah.");

    foreach (string folder in result.UnavailableFolders)
        Console.WriteLine($"  Folder tidak bisa diakses, dilewati: {folder}");

    if (result.Errors.Count > 0)
    {
        Console.WriteLine($"  {result.Errors.Count} file gagal dibaca tag-nya (tetap masuk library dengan nama file):");
        foreach (string error in result.Errors.Take(10))
            Console.WriteLine($"    {error}");
    }
}

void BrowseFolder()
{
    Console.Write("Seret folder (boleh subfolder) ke jendela ini, atau tekan Enter untuk semua lagu: ");
    string input = ReadPath();
    string? folder = input.Length == 0 ? null : input;

    var tracks = db.GetTracks(folder, limit: 100);
    PrintTracks(tracks);
    Console.WriteLine($"\nMenampilkan {tracks.Count} dari {db.CountTracks(folder)} lagu.");
}

void SearchTracks()
{
    Console.Write("Kata kunci (judul, artis, atau album): ");
    string query = Console.ReadLine()?.Trim() ?? "";
    if (query.Length == 0) return;

    var tracks = db.Search(query, limit: 50);
    PrintTracks(tracks);
    Console.WriteLine($"\n{tracks.Count} hasil.");
}

void ShowStats()
{
    var s = db.GetStats();
    Console.WriteLine($"Jumlah lagu    : {s.Tracks} ({s.Flac} FLAC, {s.Mp3} MP3)");
    Console.WriteLine($"Hi-res         : {s.HiRes} lagu");
    Console.WriteLine($"Album / artis  : {s.Albums} album, {s.Artists} artis");
    Console.WriteLine($"Total durasi   : {(int)s.TotalDuration.TotalHours} jam {s.TotalDuration.Minutes} menit");
    Console.WriteLine($"Punya cover    : {s.WithCover} lagu");
    Console.WriteLine($"Lirik tertanam : {s.WithLyrics} lagu");
}

void PrintTracks(IReadOnlyList<Track> tracks)
{
    string? currentAlbum = null;
    foreach (var t in tracks)
    {
        string year = t.Year is int y ? $" ({y})" : "";
        string album = $"{t.DisplayAlbum} — {t.AlbumArtist ?? t.DisplayArtist}{year}";
        if (album != currentAlbum)
        {
            Console.WriteLine($"\n== {album} ==");
            currentAlbum = album;
        }

        string number = t.TrackNumber is int n ? $"{n:00}." : " --";
        Console.WriteLine($"  {number} {t.DisplayTitle}  ·  {t.DisplayArtist}  [{t.QualityLabel}]  {FormatTime(t.Duration)}");
    }
}

string ReadPath() => (Console.ReadLine() ?? "").Trim().Trim('"');

static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

/// <summary>IProgress sederhana yang langsung memanggil handler di thread pelapor.</summary>
sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

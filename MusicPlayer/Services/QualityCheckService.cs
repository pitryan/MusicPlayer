using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Library;
using NAudio.Wave;

namespace MusicPlayer.Services;

/// <summary>
/// Memeriksa kualitas sumber lagu (hi-res asli, hasil upsampling, atau dari sumber lossy) dengan QualityAnalyzer, dan
/// menyimpan hasilnya di database. Bisa per lagu (popup badge kualitas) atau seluruh library di latar belakang
/// (halaman Quality Check). Dipakai bersama lewat App.QualityChecks.
/// </summary>
public sealed class QualityCheckService
{
    // Enam potongan 6 detik: cukup mewakili satu lagu, dan selesai dalam sekitar satu detik
    private static readonly TimeSpan SegmentLength = TimeSpan.FromSeconds(6);

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: sampel float di dalam WAVEFORMATEXTENSIBLE
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly LibraryDatabase _database;
    private readonly Func<string, QualityReport> _analyze;
    private readonly Dictionary<string, (FileStamp Stamp, QualityReport Report)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scan;

    /// <param name="analyze">Membaca dan menganalisis satu file; di aplikasi <see cref="AnalyzeFile"/>.</param>
    public QualityCheckService(LibraryDatabase database, Func<string, QualityReport> analyze)
    {
        _database = database;
        _analyze = analyze;
    }

    /// <summary>Satu lagu selesai diperiksa (dipicu di UI thread).</summary>
    public event EventHandler<Track>? Checked;

    /// <summary>Kemajuan pemeriksaan library berubah, atau pemeriksaannya mulai/selesai.</summary>
    public event EventHandler? ScanProgressChanged;

    public bool IsScanning => _scan is not null;

    public int ScanDone { get; private set; }

    public int ScanTotal { get; private set; }

    private static FileStamp StampOf(Track track) => new(track.FileSize, track.ModifiedUtcTicks);

    /// <summary>Hasil yang sudah ada untuk lagu ini (memori, lalu database), atau null kalau belum diperiksa.</summary>
    public QualityReport? Cached(Track track)
    {
        var stamp = StampOf(track);
        if (_cache.TryGetValue(track.FilePath, out var entry) && entry.Stamp == stamp)
            return entry.Report;

        var report = _database.GetQualityReport(track.FilePath, stamp);
        if (report is not null)
            _cache[track.FilePath] = (stamp, report);
        return report;
    }

    /// <summary>Memeriksa satu lagu dan menyimpan hasilnya.</summary>
    public async Task<QualityReport> AnalyzeAsync(Track track)
    {
        var report = await Task.Run(() =>
        {
            var result = _analyze(track.FilePath);
            _database.SaveQualityReport(track.FilePath, StampOf(track), result, DateTime.UtcNow);
            return result;
        });
        _cache[track.FilePath] = (StampOf(track), report);
        Checked?.Invoke(this, track);
        return report;
    }

    /// <summary>
    /// Lagu lossless yang belum diperiksa (atau hasilnya sudah tidak berlaku). File lossy dilewati: hasilnya sudah
    /// jelas dari formatnya.
    /// </summary>
    public IReadOnlyList<Track> TracksToCheck()
    {
        var done = _database.GetQualityReports();
        return _database.GetTracks()
            .Where(t => AudioQuality.Describe(t).IsLossless && !done.ContainsKey(t.FilePath))
            .ToList();
    }

    /// <summary>Memeriksa semua lagu yang belum diperiksa, satu per satu, sampai selesai atau dihentikan.</summary>
    public async Task ScanLibraryAsync()
    {
        if (_scan is not null)
            return;

        var scan = new CancellationTokenSource();
        _scan = scan;
        var todo = TracksToCheck();
        ScanTotal = todo.Count;
        ScanDone = 0;
        ScanProgressChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            foreach (var track in todo)
            {
                if (scan.IsCancellationRequested)
                    break;
                try
                {
                    await AnalyzeAsync(track);
                }
                catch (Exception)
                {
                    // File yang tidak bisa dibaca dilewati; lagu lain tetap diperiksa
                }
                ScanDone++;
                ScanProgressChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _scan = null;
            ScanProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Menghentikan pemeriksaan library setelah lagu yang sedang diperiksa selesai.</summary>
    public void StopScan() => _scan?.Cancel();

    /// <summary>
    /// Membaca file lewat decoder Windows yang sama dengan pemutar (jadi formatnya persis seperti yang diputar), lalu
    /// menganalisis beberapa potongannya.
    /// </summary>
    public static QualityReport AnalyzeFile(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var format = reader.WaveFormat;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat);
        int bits = format.BitsPerSample;
        var analyzer = new QualityAnalyzer(format.SampleRate, format.Channels, bits, isFloat);

        var buffer = new byte[(int)(format.SampleRate * SegmentLength.TotalSeconds) * format.BlockAlign];
        foreach (var start in QualityAnalyzer.SegmentStarts(reader.TotalTime, SegmentLength))
        {
            reader.CurrentTime = start;
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = reader.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0)
                    break;
                filled += read;
            }
            filled -= filled % format.BlockAlign;

            var bytes = buffer.AsSpan(0, filled);
            if (isFloat)
                analyzer.AddSegment(PcmDecode.ToFloats(bytes));
            else
                analyzer.AddSegment(PcmDecode.ToInts(bytes, bits));
        }
        return analyzer.Finish();
    }
}

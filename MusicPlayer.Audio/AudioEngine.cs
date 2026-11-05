using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MusicPlayer.Audio;

/// <summary>Cara volume diatur untuk lagu yang sedang diputar.</summary>
public enum VolumeKind
{
    /// <summary>Shared mode: volume aplikasi ini di Volume Mixer Windows.</summary>
    Session,

    /// <summary>Exclusive mode, perangkat punya volume hardware: volume DAC/speaker diatur langsung (tetap bit-perfect).</summary>
    Device,

    /// <summary>Exclusive mode, perangkat tanpa volume hardware: sampel dikecilkan secara digital.</summary>
    Digital,
}

/// <summary>
/// Engine pemutar audio: decode file lewat Media Foundation, lalu putar lewat WASAPI.
/// Selalu mencoba exclusive mode lebih dulu; kalau tidak bisa, otomatis turun ke shared mode.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int LatencyMs = 100;

    /// <summary>
    /// Waktu yang cukup bagi perangkat untuk memutar sisa audio di buffernya (kira-kira dua kali latency).
    /// Ditunggu sebelum player dihentikan di akhir lagu, dan sebelum perangkat dilepas saat pause.
    /// </summary>
    public static readonly TimeSpan DrainTime = TimeSpan.FromMilliseconds(LatencyMs * 2 + 50);

    private readonly object _playerLock = new();
    private MediaFoundationReader? _reader;
    private PcmOutputProvider? _output;
    private WasapiPlayer? _player;
    private MMDevice? _device;
    private string? _path;

    // Seek yang sedang disiapkan (lihat SeekAsync): nomor permintaan terakhir, dan posisi tujuannya untuk Position.
    // Close menaikkan nomornya, supaya reader yang selesai disiapkan setelah lagu ditutup atau diganti dibuang.
    private int _seekRequest;
    private TimeSpan? _pendingSeek;

    // Lagu berikutnya untuk gapless (lihat PrepareNextAsync): path yang sudah diputuskan, reader-nya kalau bisa menyambung
    // tanpa jeda, dan nomor permintaan terakhir (Close dan CancelNext menaikkannya, seperti _seekRequest). Setelah lagu itu
    // mulai di thread audio, path-nya menunggu di _unannounced sampai NextStarted dipicu di thread pemanggil.
    private string? _nextPath;
    private MediaFoundationReader? _next;
    private int _nextRequest;
    private string? _unannounced;

    // Thread yang membuat engine ini (UI thread), untuk NextStarted. Null di luar aplikasi UI (tes): thread pool.
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    /// <summary>
    /// Lagu berikutnya yang disiapkan (<see cref="PrepareNextAsync"/>) sudah menyambung tanpa jeda, dengan path-nya. Sejak itu
    /// lagu itulah lagu yang terbuka. Dipicu di thread yang membuat engine ini, sama seperti <see cref="PlaybackStopped"/>.
    /// </summary>
    public event EventHandler<string>? NextStarted;

    /// <summary>
    /// Lagu berikutnya yang sudah diputuskan lewat <see cref="PrepareNextAsync"/>, entah bisa menyambung tanpa jeda atau tidak
    /// (format berbeda, atau file-nya tidak bisa dibuka). Null kalau belum ada, atau setelah dibatalkan atau mulai diputar.
    /// </summary>
    public string? NextPath => _nextPath;

    public WaveFormat? SourceFormat => _reader?.WaveFormat;
    public string OutputDescription { get; private set; } = "";
    public bool IsExclusive { get; private set; }
    public string? FallbackReason { get; private set; }

    /// <summary>Format yang dikirim ke perangkat dalam exclusive mode; null kalau memakai shared mode.</summary>
    public PcmFormat? ExclusiveFormat { get; private set; }

    /// <summary>Cara volume diatur untuk lagu ini, ditentukan saat perangkat dibuka.</summary>
    public VolumeKind VolumeKind { get; private set; }

    /// <summary>
    /// True kalau lagu masih terbuka (file dan posisinya tersimpan), tapi perangkat audio sudah dilepas.
    /// Lihat <see cref="ReleaseDevice"/>.
    /// </summary>
    public bool IsDeviceReleased => _output is not null && _player is null;

    /// <summary>
    /// True selama ada lagu yang terbuka (setelah <see cref="Open"/> atau <see cref="OpenWithoutDevice"/>, sampai
    /// <see cref="Close"/>), dengan atau tanpa perangkat audio. False kalau belum ada lagu, atau kalau pembukaan
    /// terakhir gagal (Open menutup lagu lama lebih dulu, jadi lagu lama juga sudah tidak terbuka).
    /// </summary>
    public bool IsOpen => _output is not null;

    /// <summary>Posisi lagu; selama seek disiapkan, posisi tujuannya (lihat <see cref="SeekAsync"/>).</summary>
    public TimeSpan Position => _pendingSeek ?? _output?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _output?.TotalTime ?? TimeSpan.Zero;

    public PlaybackState State
    {
        get
        {
            if (_output is null) return PlaybackState.Stopped;
            if (_player is null) return PlaybackState.Paused; // perangkat dilepas: lagu tetap di posisinya
            if (_player.PlaybackState == PlaybackState.Playing && _output.IsPaused) return PlaybackState.Paused;
            return _player.PlaybackState;
        }
    }

    public void Open(string path, MMDevice device, bool preferExclusive = true)
    {
        Close();
        _reader = CreateReader(path);
        _path = path;

        try
        {
            OpenDevice(device, preferExclusive);
        }
        catch
        {
            // Jangan tinggalkan file terbuka tanpa perangkat. Pesan error-nya ditampilkan pemanggil.
            Close();
            throw;
        }
    }

    /// <summary>
    /// Membuka lagu tanpa perangkat audio, dalam keadaan sama seperti setelah <see cref="ReleaseDevice"/>: posisi
    /// dan durasi sudah terbaca, dan <see cref="ReopenDevice"/> membuka perangkatnya nanti. Dipakai kalau perangkat
    /// pilihan pengguna sedang tidak tersambung, supaya lagunya tetap tampil dan bisa diputar di perangkat lain.
    /// </summary>
    public void OpenWithoutDevice(string path)
    {
        Close();
        _reader = CreateReader(path);
        _path = path;

        // Provider sementara, hanya untuk membaca posisi dan durasi. ReopenDevice membuat provider baru dengan
        // format yang diterima perangkatnya.
        _output = new PcmOutputProvider(_reader, _reader.WaveFormat) { IsPaused = true };
        _output.NextStarted += OnNextStarted;
    }

    /// <summary>Provider lagu sekarang, untuk tes yang membacanya seperti thread audio.</summary>
    internal PcmOutputProvider? Output => _output;

    /// <summary>
    /// Melepas perangkat audio tanpa menutup lagu: file dan posisinya tetap disimpan. Selama itu aplikasi
    /// lain bisa memakai DAC dan Windows bisa sleep. <see cref="ReopenDevice"/> membukanya lagi.
    /// </summary>
    /// <remarks>
    /// Saat pause, panggil ini setelah <see cref="DrainTime"/>. Sisa audio di buffer perangkat sempat
    /// terdengar lebih dulu, jadi posisi yang tersimpan sama dengan bagian terakhir yang benar-benar terdengar,
    /// dan lagu nanti lanjut tanpa ada bagian yang terlompati. Dipanggil juga setelah perangkat error
    /// (misalnya DAC dicabut), supaya player yang sudah mati dibuang.
    /// </remarks>
    public void ReleaseDevice()
    {
        lock (_playerLock)
        {
            DisposePlayer();
            if (_output is not null)
                _output.IsPaused = true;
        }
    }

    /// <summary>
    /// Membuka perangkat lagi setelah <see cref="ReleaseDevice"/>, untuk lagu yang sama di posisi yang sama.
    /// Format dinegosiasi ulang karena perangkatnya bisa berbeda (misalnya DAC dicabut, lalu Windows pindah
    /// ke speaker). Kalau gagal, engine tetap dalam keadaan dilepas di posisi yang sama, jadi bisa dicoba lagi.
    /// </summary>
    public void ReopenDevice(MMDevice device, bool preferExclusive = true)
    {
        if (_reader is null || _output is null)
            throw new InvalidOperationException("No song is open.");

        ReleaseDevice();
        ApplyNextIfStarted(); // provider baru harus membaca lagu yang benar-benar sedang terbuka
        var previousDevice = _device;

        try
        {
            OpenDevice(device, preferExclusive);
        }
        catch
        {
            // Perangkat yang gagal dibuka akan di-dispose pemanggil, jadi tidak boleh disimpan. Perangkat lama
            // tetap dipakai untuk mengatur volume selama lagu masih di-pause.
            _device = previousDevice;
            throw;
        }
    }

    private void OpenDevice(MMDevice device, bool preferExclusive)
    {
        _device = device;
        FallbackReason = null;
        ExclusiveFormat = null;

        if (preferExclusive && TryOpenExclusive(device))
        {
            VolumeKind = HasHardwareVolume(device) ? VolumeKind.Device : VolumeKind.Digital;
            return;
        }

        // Shared mode: mixer Windows yang mengurus konversi format
        CreatePlayer(device, exclusive: false, _reader!.WaveFormat);
        IsExclusive = false;
        OutputDescription = "Windows mixer (shared mode)";
        VolumeKind = VolumeKind.Session;
    }

    /// <summary>
    /// Mengatur volume (0 sampai 1) sesuai <see cref="VolumeKind"/>. Untuk VolumeKind.Device,
    /// yang diubah adalah volume perangkat itu sendiri, sama seperti slider volume Windows.
    /// </summary>
    public void SetVolume(float volume, bool muted)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        try
        {
            switch (VolumeKind)
            {
                case VolumeKind.Device when _device is not null:
                    _device.AudioEndpointVolume.Mute = muted;
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                    break;
                case VolumeKind.Digital when _output is not null:
                    _output.Gain = muted ? 0f : volume;
                    break;
                case VolumeKind.Session when _player is not null:
                    _player.IsMuted = muted;
                    _player.Volume = volume;
                    break;
            }
        }
        catch (COMException)
        {
            // Perangkat dicabut atau menolak perubahan volume: abaikan, pemutaran tetap jalan
        }
    }

    private static bool HasHardwareVolume(MMDevice device)
    {
        try
        {
            return device.AudioEndpointVolume.HardwareSupport.HasFlag(EEndpointHardwareSupport.Volume);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private bool TryOpenExclusive(MMDevice device)
    {
        var (format, reason) = ExclusiveFormatNegotiator.Find(device, _reader!.WaveFormat);
        if (format is null)
        {
            FallbackReason = reason;
            return false;
        }

        try
        {
            CreatePlayer(device, exclusive: true, format.ToWaveFormat());
            IsExclusive = true;
            ExclusiveFormat = format;
            OutputDescription = format.ToString();
            return true;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
            FallbackReason = ex is COMException com ? ExclusiveFormatNegotiator.DescribeError(com) : ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Membuka perangkat dengan format tertentu. Kalau gagal, tidak ada yang berubah: player yang setengah
    /// jadi langsung dibuang, dan provider lama (beserta posisinya) tetap dipakai.
    /// </summary>
    private void CreatePlayer(MMDevice device, bool exclusive, WaveFormat outputFormat)
    {
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithEventSync()
            .WithLatency(LatencyMs)
            .WithMmcssThreadPriority();

        builder = exclusive ? builder.WithExclusiveMode() : builder.WithSharedMode();

        // Provider baru membaca dari reader yang sama, jadi posisi lagu tidak berubah. Provider lama baru
        // diganti setelah perangkat berhasil dibuka, supaya posisinya tetap terbaca kalau pembukaan gagal.
        var output = new PcmOutputProvider(_reader!, outputFormat);
        if (_pendingSeek is not null)
            output.BeginSeek(); // reader lama belum di posisi tujuan: hening sampai reader barunya terpasang
        var player = builder.Build();
        try
        {
            player.Init(output); // di sinilah error "device in use" atau format ditolak muncul
        }
        catch
        {
            player.Dispose();
            throw;
        }

        // Lagu berikutnya yang sudah disiapkan ikut pindah ke provider baru (perangkat dibuka lagi setelah pause, atau output diganti)
        if (_next is not null)
            output.ReplaceNext(null, _next);

        output.EndOfStream += OnEndOfStream;
        output.NextStarted += OnNextStarted;
        player.PlaybackStopped += OnPlaybackStopped;
        lock (_playerLock)
        {
            if (_output is not null)
            {
                _output.EndOfStream -= OnEndOfStream;
                _output.NextStarted -= OnNextStarted;
            }
            _output = output;
            _player = player;
        }
    }

    public void Play()
    {
        // Perangkat sudah dilepas (lihat ReleaseDevice): pemanggil harus membukanya lagi dengan ReopenDevice
        if (_player is null || _output is null) return;
        _output.IsPaused = false;
        _player.Play();

        // Lagu di-pause setelah datanya habis (lihat StopAfterDrain): selesaikan seperti lagu yang habis biasa,
        // supaya PlaybackStopped terpicu dan pemanggil pindah ke lagu berikutnya (atau mengulang, kalau Repeat One).
        // EndOfStream tidak akan terpicu lagi, karena provider yang sudah habis hanya mengirim hening.
        if (_output.HasEnded)
            StopAfterDrain(_output);
    }

    public void Pause()
    {
        if (_output is not null)
            _output.IsPaused = true;
    }

    public void TogglePause()
    {
        if (State == PlaybackState.Playing) Pause();
        else Play();
    }

    /// <summary>
    /// Lompat ke posisi tertentu. Media Foundation tidak bisa seek dengan tepat di tengah pemutaran untuk file seperti FLAC
    /// tanpa SEEKTABLE: audionya bisa meleset belasan detik, dan timestamp-nya pun ikut salah, jadi tidak bisa dikoreksi.
    /// Reader yang baru dibuka selalu tepat, tapi butuh waktu sebanding dengan posisinya (lebih dari 1 detik di akhir file
    /// hi-res). Karena itu reader baru disiapkan di thread lain sementara output hening, lalu dipasang begitu siap. Selama
    /// itu <see cref="Position"/> sudah menunjukkan posisi tujuan, dan seek berikutnya membatalkan yang belum selesai.
    /// </summary>
    /// <remarks>
    /// Dipanggil dari thread yang sama dengan method lain (UI thread). Lanjutannya kembali ke thread itu lewat
    /// SynchronizationContext, jadi reader dipasang tanpa bersaing dengan Open, Close, atau ReopenDevice. Tidak pernah gagal:
    /// kalau file-nya tidak bisa dibuka lagi, seek dibatalkan dan lagu lanjut dari posisinya semula.
    /// </remarks>
    public async Task SeekAsync(TimeSpan position)
    {
        ApplyNextIfStarted(); // seek berlaku untuk lagu yang sedang berbunyi
        if (_output is null || _path is null)
            return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > Duration) position = Duration;

        int request = ++_seekRequest;
        _pendingSeek = position;
        _output.BeginSeek();

        string path = _path;
        MediaFoundationReader? fresh;
        try
        {
            fresh = await Task.Run(() => OpenReader(path, position));
        }
        catch (Exception)
        {
            fresh = null;
        }

        // Selama reader disiapkan, pengguna bisa seek lagi, atau lagunya ditutup atau diganti: hasil yang usang dibuang
        if (request != _seekRequest || _output is null)
        {
            fresh?.Dispose();
            return;
        }

        _pendingSeek = null;
        if (fresh is null)
        {
            // File-nya tidak bisa dibuka lagi, misalnya baru saja dipindah. Reader lama tidak di-seek: objek Media Foundation-nya
            // dibuat di thread audio, dan di reader yang belum pernah dibaca, seek justru membuka file-nya dari path lagi. Lagu
            // tetap di posisinya.
            _output.EndSeek();
            return;
        }

        // _output bisa provider lain dari yang tadi (ReopenDevice selama menunggu), tapi selalu membaca dari _reader
        var old = _reader;
        _output.EndSeek(fresh);
        _reader = fresh;
        old?.Dispose();
    }

    /// <summary>
    /// Reader untuk lagu yang dibuka dari UI thread. Objek Media Foundation-nya baru dibuat di bacaan pertama, di thread audio,
    /// supaya objek yang dibuat di UI thread (STA) tidak dipakai dari thread lain.
    /// </summary>
    private static MediaFoundationReader CreateReader(string path) =>
        new(path, new MediaFoundationReader.MediaFoundationReaderSettings { RepositionInRead = false });

    /// <summary>
    /// Reader baru yang sudah berada tepat di <paramref name="position"/>, untuk disiapkan di thread pool (seek, lagu
    /// berikutnya). Setter CurrentTime sekaligus membuat objek Media Foundation-nya di thread ini, dan dengan
    /// RepositionInRead = false pekerjaan seek yang lama juga dikerjakan di sini, jadi bacaan pertama di thread audio langsung
    /// jalan. Tanpa itu, bacaan pertama untuk FLAC butuh 4 sampai 20 ms (diukur di NVMe), dan lebih lama di disk yang lambat.
    /// </summary>
    internal static MediaFoundationReader OpenReader(string path, TimeSpan position)
    {
        var reader = CreateReader(path);
        try
        {
            reader.CurrentTime = position;
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Menyiapkan lagu berikutnya supaya menyambung tanpa jeda (gapless) begitu lagu sekarang habis: file-nya dibuka di thread
    /// lain, lalu dipasang di provider. Hanya kalau formatnya sama persis dengan lagu sekarang, jadi perangkat tidak perlu dibuka
    /// ulang dan sampelnya tetap tidak diubah (bit-perfect). Lagu dengan sample rate atau bit depth lain tetap diputar seperti
    /// biasa setelah lagu ini berhenti, dengan perangkat yang dibuka ulang untuk formatnya.
    /// </summary>
    /// <remarks>
    /// Dipanggil dari thread yang sama dengan method lain. Lagu berikutnya yang lama dibatalkan lebih dulu. Lagu itu menjadi
    /// <see cref="NextPath"/>, juga kalau ternyata tidak bisa menyambung, supaya pemanggil tidak terus mencobanya lagi.
    /// </remarks>
    /// <returns>True kalau lagu itu akan menyambung tanpa jeda.</returns>
    public async Task<bool> PrepareNextAsync(string path)
    {
        CancelNext();

        // Lagu berikutnya yang lama baru saja mulai, tapi pemanggil belum tahu (NextStarted belum dipicu): path ini dipilih untuk
        // lagu sebelumnya. Pemanggil memilih lagi setelah NextStarted.
        if (_output is null || _reader is null || _unannounced is not null)
            return false;

        int request = ++_nextRequest;
        _nextPath = path;

        MediaFoundationReader? next;
        try
        {
            next = await Task.Run(() => OpenReader(path, TimeSpan.Zero));
        }
        catch (Exception)
        {
            return false; // diputar seperti biasa nanti, dan error-nya ditampilkan di situ
        }

        // Sementara itu lagu lain dibuka, lagu berikutnya diganti, atau lagu ini ditutup
        if (request != _nextRequest || _output is null || _reader is null
            || !PcmOutputProvider.HasSameFormat(_reader.WaveFormat, next.WaveFormat)
            || !_output.ReplaceNext(null, next))
        {
            next.Dispose();
            return false;
        }

        _next = next;
        return true;
    }

    /// <summary>
    /// Membatalkan lagu berikutnya (antrean berubah, atau file-nya akan ditulis). Lagu yang sudah mulai menyambung tidak bisa
    /// dibatalkan lagi: lagu itu sudah berbunyi, dan <see cref="NextStarted"/> tetap dipicu.
    /// </summary>
    public void CancelNext()
    {
        _nextRequest++; // persiapan yang masih berjalan dibuang (lihat PrepareNextAsync)
        if (_next is not null && _output is not null && !_output.ReplaceNext(_next, null))
        {
            // Sudah mulai berbunyi di thread audio: menjadi lagu yang terbuka, dan menunggu diumumkan
            ApplyNextIfStarted();
            return;
        }

        _next?.Dispose(); // file-nya langsung dilepas
        _next = null;
        _nextPath = null;
    }

    /// <summary>
    /// Lagu berikutnya sudah mulai dibaca di thread audio: sejak itu lagu itulah lagu yang terbuka (reader, path, seek), dan
    /// reader lagu sebelumnya tidak dipakai lagi. NextStarted dipicu belakangan, lewat <see cref="AnnounceNext"/>.
    /// </summary>
    private void ApplyNextIfStarted()
    {
        if (_next is null || _output is null || !ReferenceEquals(_output.Source, _next))
            return;

        var finished = _reader;
        _reader = _next;
        _path = _nextPath;
        _unannounced = _nextPath;
        _next = null;
        _nextPath = null;
        finished?.Dispose();
    }

    // Dari thread audio: diteruskan ke thread pemanggil, tempat semua keadaan engine diubah
    private void OnNextStarted(object? sender, EventArgs e)
    {
        if (_context is { } context)
            context.Post(_ => AnnounceNext(), null);
        else
            ThreadPool.QueueUserWorkItem(_ => AnnounceNext());
    }

    private void AnnounceNext()
    {
        ApplyNextIfStarted();

        // Kosong kalau sementara itu lagu sudah ditutup atau lagu lain dibuka
        if (_unannounced is not { } path)
            return;
        _unannounced = null;
        NextStarted?.Invoke(this, path);
    }

    public void Close()
    {
        lock (_playerLock)
        {
            DisposePlayer();
            if (_output is not null)
            {
                _output.EndOfStream -= OnEndOfStream;
                _output.NextStarted -= OnNextStarted;
            }
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _path = null;
        _seekRequest++; // seek yang belum selesai tidak dipasang lagi (lihat SeekAsync)
        _pendingSeek = null;

        // Lagu berikutnya ikut ditutup, juga kalau sudah mulai berbunyi tapi belum diumumkan (lagu lain dibuka tepat saat itu)
        _nextRequest++;
        _next?.Dispose();
        _next = null;
        _nextPath = null;
        _unannounced = null;
        _device = null; // perangkatnya milik pemanggil, jadi tidak di-dispose di sini
    }

    public void Dispose() => Close();

    private void OnEndOfStream(object? sender, EventArgs e) => StopAfterDrain((PcmOutputProvider)sender!);

    /// <summary>
    /// Menghentikan player setelah sisa audio di buffer perangkat selesai diputar. Player yang berhenti memicu
    /// PlaybackStopped, dan dari situ pemanggil pindah ke lagu berikutnya.
    /// </summary>
    private void StopAfterDrain(PcmOutputProvider output)
    {
        // EndOfStream datang dari thread audio. Menghentikan player dari thread itu sendiri
        // akan deadlock, jadi kita jadwalkan di thread lain.
        _ = Task.Run(async () =>
        {
            await Task.Delay(DrainTime);
            lock (_playerLock)
            {
                // Lewati kalau sementara itu file sudah diganti atau pengguna seek mundur. Lewati juga kalau
                // pengguna menekan pause: lagunya tetap di-pause di akhir, dan Play nanti yang menyelesaikannya.
                if (ReferenceEquals(_output, output) && output.HasEnded && !output.IsPaused)
                    _player?.Stop();
            }
        });
    }

    private void DisposePlayer()
    {
        lock (_playerLock)
        {
            if (_player is null) return;

            // Lepas handler lebih dulu: Dispose ikut memicu PlaybackStopped, dan event itu tidak boleh
            // dianggap "lagu selesai" (yang membuat antrean maju ke lagu berikutnya)
            _player.PlaybackStopped -= OnPlaybackStopped;
            _player.Dispose();
            _player = null;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Di aplikasi UI, event ini dikirim lewat antrean UI thread, jadi bisa datang terlambat
        // dari player lama yang sudah diganti lagu lain. Event seperti itu diabaikan.
        if (!ReferenceEquals(sender, _player))
            return;

        PlaybackStopped?.Invoke(this, e);
    }
}

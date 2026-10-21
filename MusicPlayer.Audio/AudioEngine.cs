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

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

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

    public TimeSpan Position => _output?.CurrentTime ?? TimeSpan.Zero;
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
        _reader = new MediaFoundationReader(path);

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
        _reader = new MediaFoundationReader(path);

        // Provider sementara, hanya untuk membaca posisi dan durasi. ReopenDevice membuat provider baru dengan
        // format yang diterima perangkatnya.
        _output = new PcmOutputProvider(_reader, _reader.WaveFormat) { IsPaused = true };
    }

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

        output.EndOfStream += OnEndOfStream;
        player.PlaybackStopped += OnPlaybackStopped;
        lock (_playerLock)
        {
            if (_output is not null)
                _output.EndOfStream -= OnEndOfStream;
            _output = output;
            _player = player;
        }
    }

    public void Play()
    {
        // Perangkat sudah dilepas (lihat ReleaseDevice): pemanggil harus membukanya lagi dengan ReopenDevice
        if (_player is null || _output is null) return;
        if (_output.HasEnded)
            _output.CurrentTime = TimeSpan.Zero; // lagu sudah habis: putar ulang dari awal
        _output.IsPaused = false;
        _player.Play();
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

    public void Seek(TimeSpan position)
    {
        if (_output is null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > Duration) position = Duration;
        _output.CurrentTime = position;
    }

    public void Close()
    {
        lock (_playerLock)
        {
            DisposePlayer();
            if (_output is not null)
                _output.EndOfStream -= OnEndOfStream;
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _device = null; // perangkatnya milik pemanggil, jadi tidak di-dispose di sini
    }

    public void Dispose() => Close();

    private void OnEndOfStream(object? sender, EventArgs e)
    {
        var output = (PcmOutputProvider)sender!;

        // Event ini datang dari thread audio. Menghentikan player dari thread itu sendiri
        // akan deadlock, jadi kita jadwalkan di thread lain, setelah sisa audio di buffer
        // perangkat selesai diputar.
        _ = Task.Run(async () =>
        {
            await Task.Delay(DrainTime);
            lock (_playerLock)
            {
                // Lewati kalau sementara itu file sudah diganti atau pengguna seek mundur
                if (ReferenceEquals(_output, output) && output.HasEnded)
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

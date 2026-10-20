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

    /// <summary>Cara volume diatur untuk lagu ini, ditentukan saat file dibuka.</summary>
    public VolumeKind VolumeKind { get; private set; }

    public TimeSpan Position => _output?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _output?.TotalTime ?? TimeSpan.Zero;

    public PlaybackState State
    {
        get
        {
            if (_player is null || _output is null) return PlaybackState.Stopped;
            if (_player.PlaybackState == PlaybackState.Playing && _output.IsPaused) return PlaybackState.Paused;
            return _player.PlaybackState;
        }
    }

    public void Open(string path, MMDevice device, bool preferExclusive = true)
    {
        Close();
        _device = device;
        _reader = new MediaFoundationReader(path);
        FallbackReason = null;
        ExclusiveFormat = null;

        if (preferExclusive && TryOpenExclusive(device))
        {
            VolumeKind = HasHardwareVolume(device) ? VolumeKind.Device : VolumeKind.Digital;
            return;
        }

        // Shared mode: mixer Windows yang mengurus konversi format
        CreatePlayer(device, exclusive: false, _reader.WaveFormat);
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
            DisposePlayer();
            FallbackReason = ex is COMException com ? ExclusiveFormatNegotiator.DescribeError(com) : ex.Message;
            return false;
        }
    }

    private void CreatePlayer(MMDevice device, bool exclusive, WaveFormat outputFormat)
    {
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithEventSync()
            .WithLatency(LatencyMs)
            .WithMmcssThreadPriority();

        builder = exclusive ? builder.WithExclusiveMode() : builder.WithSharedMode();

        _output = new PcmOutputProvider(_reader!, outputFormat);
        _output.EndOfStream += OnEndOfStream;
        _player = builder.Build();
        _player.PlaybackStopped += OnPlaybackStopped;
        _player.Init(_output);
    }

    public void Play()
    {
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
        // perangkat selesai diputar (kira-kira dua kali latency).
        _ = Task.Run(async () =>
        {
            await Task.Delay(LatencyMs * 2 + 50);
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

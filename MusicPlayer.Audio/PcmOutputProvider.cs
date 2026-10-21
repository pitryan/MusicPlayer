using System.Buffers.Binary;
using NAudio.Wave;

namespace MusicPlayer.Audio;

/// <summary>
/// Penghubung antara decoder dan WASAPI. Tugasnya:
/// 1. Mengubah wadah bit depth kalau perlu (misalnya 16 → 24-bit) tanpa mengubah isi sampel.
/// 2. Membuat seek aman dipanggil dari thread lain selama audio diputar.
/// 3. Mengirim hening saat pause dan setelah lagu habis (alasannya ada di <see cref="IsPaused"/>
///    dan <see cref="EndOfStream"/>).
/// </summary>
public sealed class PcmOutputProvider : IWaveProvider
{
    private readonly WaveStream _source;
    private readonly object _gate = new();
    private readonly int _inBytes;
    private readonly int _outBytes;
    private byte[] _sourceBuffer = [];
    private volatile bool _isPaused;
    private volatile bool _hasEnded;
    private volatile float _gain = 1f;

    public PcmOutputProvider(WaveStream source, WaveFormat outputFormat)
    {
        _source = source;
        WaveFormat = outputFormat;
        _inBytes = source.WaveFormat.BitsPerSample / 8;
        _outBytes = outputFormat.BitsPerSample / 8;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Dipicu sekali, dari thread audio, saat data lagu habis.
    /// Kita sengaja tidak mengembalikan 0 ke WasapiPlayer untuk menandai akhir lagu. Di exclusive
    /// mode, NAudio 3.1 lalu memanggil ReleaseBuffer dengan 0 frame, padahal WASAPI mewajibkan
    /// satu buffer penuh, sehingga muncul error "A buffer size error occurred"
    /// (AUDCLNT_E_BUFFER_SIZE_ERROR). Sebagai gantinya kita terus mengirim hening, dan
    /// AudioEngine yang menghentikan pemutaran.
    /// </summary>
    public event EventHandler? EndOfStream;

    /// <summary>True setelah data lagu habis. Di-reset kalau ada seek.</summary>
    public bool HasEnded => _hasEnded;

    /// <summary>
    /// Dalam exclusive mode, perangkat memutar ulang isi buffer lama kalau kita berhenti
    /// mengisinya, sehingga terdengar potongan suara yang berulang. Karena itu saat pause
    /// kita tetap mengirim data, tapi berupa hening.
    /// Hening ini hanya sebentar: begitu sisa audio di buffer perangkat selesai diputar,
    /// PlaybackService melepas perangkatnya (AudioEngine.ReleaseDevice), supaya Windows bisa
    /// sleep dan aplikasi lain bisa memakai DAC.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    /// <summary>
    /// Volume digital, dari 0 (hening) sampai 1 (apa adanya). Hanya dipakai untuk perangkat tanpa volume
    /// hardware di exclusive mode. Di bawah 1, sampel ikut diubah, jadi output tidak lagi bit-perfect;
    /// tepat di 1, sampel tidak disentuh sama sekali.
    /// </summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 1f);
    }

    public TimeSpan TotalTime => _source.TotalTime;

    public TimeSpan CurrentTime
    {
        get
        {
            lock (_gate) return _source.CurrentTime;
        }
        set
        {
            lock (_gate)
            {
                var format = _source.WaveFormat;
                long bytes = (long)(value.TotalSeconds * format.AverageBytesPerSecond);
                bytes -= bytes % format.BlockAlign; // harus tepat di awal frame
                _source.Position = Math.Clamp(bytes, 0, _source.Length);
                _hasEnded = false;
            }
        }
    }

    public int Read(Span<byte> buffer)
    {
        if (_isPaused || _hasEnded)
        {
            buffer.Clear();
            return buffer.Length;
        }

        int total = 0;
        bool justEnded = false;

        lock (_gate)
        {
            int frameBytes = WaveFormat.BlockAlign;
            while (buffer.Length - total >= frameBytes)
            {
                int read = ReadConverted(buffer[total..]);
                if (read == 0)
                {
                    _hasEnded = justEnded = true;
                    break;
                }
                total += read;
            }
        }

        if (_gain < 1f)
            ApplyGain(buffer[..total]);

        if (!justEnded)
            return total;

        // Lagu habis: isi sisa buffer dengan hening, lalu kabari engine
        buffer[total..].Clear();
        EndOfStream?.Invoke(this, EventArgs.Empty);
        return buffer.Length;
    }

    private int ReadConverted(Span<byte> buffer)
    {
        // Wadah sama besar: tidak perlu konversi, langsung baca ke buffer tujuan
        if (_inBytes == _outBytes)
            return _source.Read(buffer);

        int samples = buffer.Length / _outBytes;
        samples -= samples % WaveFormat.Channels; // hanya frame utuh
        int bytesWanted = samples * _inBytes;

        if (_sourceBuffer.Length < bytesWanted)
            _sourceBuffer = new byte[bytesWanted];

        int samplesRead = _source.Read(_sourceBuffer.AsSpan(0, bytesWanted)) / _inBytes;
        ReadOnlySpan<byte> input = _sourceBuffer;

        for (int i = 0; i < samplesRead; i++)
        {
            int sample = ReadSample(input.Slice(i * _inBytes, _inBytes));
            WriteSample(buffer.Slice(i * _outBytes, _outBytes), sample);
        }

        return samplesRead * _outBytes;
    }

    private void ApplyGain(Span<byte> data)
    {
        double gain = _gain;
        for (int i = 0; i + _outBytes <= data.Length; i += _outBytes)
        {
            var slot = data.Slice(i, _outBytes);
            WriteSample(slot, (int)Math.Round(ReadSample(slot) * gain));
        }
    }

    /// <summary>Membaca satu sampel dan menaruhnya "rata kiri" di dalam int 32-bit.</summary>
    private static int ReadSample(ReadOnlySpan<byte> s) => s.Length switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(s) << 16,
        3 => (s[0] << 8) | (s[1] << 16) | (s[2] << 24),
        _ => BinaryPrimitives.ReadInt32LittleEndian(s),
    };

    /// <summary>Menulis sampel rata kiri ke wadah tujuan. Bit bawah yang tersisa otomatis bernilai nol.</summary>
    private static void WriteSample(Span<byte> d, int sample)
    {
        switch (d.Length)
        {
            case 2:
                BinaryPrimitives.WriteInt16LittleEndian(d, (short)(sample >> 16));
                break;
            case 3:
                d[0] = (byte)(sample >> 8);
                d[1] = (byte)(sample >> 16);
                d[2] = (byte)(sample >> 24);
                break;
            default:
                BinaryPrimitives.WriteInt32LittleEndian(d, sample);
                break;
        }
    }
}

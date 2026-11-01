using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// Folder sementara berisi file WAV buatan sendiri (nada sinus), supaya tes tidak butuh file lagu sungguhan.
/// Dihapus lagi setelah tes selesai.
/// </summary>
public sealed class TempAudioFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MusicPlayer.Audio.Tests", Guid.NewGuid().ToString("N"));

    public TempAudioFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Membuat WAV 16-bit 44,1 kHz dengan panjang dan jumlah channel tertentu.</summary>
    public string CreateWav(string name, TimeSpan duration, int channels = 2) =>
        CreateWav(name, duration, new WaveFormat(44100, 16, channels));

    /// <summary>Membuat WAV 16-bit dengan format tertentu, misalnya WaveFormatExtensible dengan speaker mask sendiri.</summary>
    public string CreateWav(string name, TimeSpan duration, WaveFormat format)
    {
        if (format.BitsPerSample != 16)
            throw new ArgumentException("Only 16-bit test files are supported.", nameof(format));

        string path = File(name);
        using var writer = new WaveFileWriter(path, format);

        int frames = (int)(duration.TotalSeconds * format.SampleRate);
        var frame = new byte[format.BlockAlign];
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 8000);
            for (int c = 0; c < format.Channels; c++)
            {
                frame[c * 2] = (byte)sample;
                frame[c * 2 + 1] = (byte)(sample >> 8);
            }
            writer.Write(frame, 0, frame.Length);
        }
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

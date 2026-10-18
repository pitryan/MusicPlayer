using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MusicPlayer.Audio;

/// <summary>
/// Mencari format yang diterima perangkat dalam WASAPI exclusive mode, tanpa mengubah
/// sample rate dan jumlah channel. Yang boleh berubah hanya "wadah" bit depth
/// (misalnya 16-bit dikirim sebagai 24-bit), dan itu tetap lossless.
/// </summary>
/// <remarks>
/// NAudio sebenarnya punya negosiasi otomatis, tapi konversinya lewat float dan tidak
/// mencoba "24-bit dalam wadah 32-bit" yang dipakai banyak DAC USB. Karena kita ingin
/// bit-perfect, negosiasinya kita tulis sendiri.
/// </remarks>
public static class ExclusiveFormatNegotiator
{
    public static (PcmFormat? Format, string? Reason) Find(MMDevice device, WaveFormat source)
    {
        int bits = source.BitsPerSample;
        if (source.Encoding != WaveFormatEncoding.Pcm || bits is not (16 or 24 or 32))
            return (null, $"Hasil decode berformat {source.Encoding} {bits}-bit, belum didukung untuk exclusive mode.");

        using var client = device.CreateAudioClient();

        foreach (var candidate in GetCandidates(source.SampleRate, source.Channels, bits))
        {
            try
            {
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, candidate.ToWaveFormat()))
                    return (candidate, null);
            }
            catch (COMException ex) when (ex.HResult == AudioClientErrorCode.ExclusiveModeNotAllowed)
            {
                return (null, DescribeError(ex));
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                // Sebagian driver menolak format asing dengan error, bukan dengan jawaban
                // "tidak didukung". Anggap tidak didukung, lalu coba kandidat berikutnya.
            }
        }

        return (null, $"Perangkat tidak mendukung {source.SampleRate / 1000.0:0.#} kHz / {source.Channels} ch dalam exclusive mode.");
    }

    /// <summary>
    /// Urutan prioritas: format asli lebih dulu, lalu wadah yang lebih besar.
    /// Wadah yang lebih kecil (misalnya 24 → 16-bit) tidak pernah dicoba karena membuang data.
    /// </summary>
    private static IEnumerable<PcmFormat> GetCandidates(int sampleRate, int channels, int bits)
    {
        if (bits is 16 or 24) yield return new PcmFormat(sampleRate, channels, bits, bits);
        if (bits == 16) yield return new PcmFormat(sampleRate, channels, 24, 24);
        if (bits <= 24) yield return new PcmFormat(sampleRate, channels, 32, 24);
        yield return new PcmFormat(sampleRate, channels, 32, 32);
    }

    public static string DescribeError(COMException ex) => ex.HResult switch
    {
        AudioClientErrorCode.DeviceInUse => "Perangkat sedang dipakai aplikasi lain dalam exclusive mode.",
        AudioClientErrorCode.ExclusiveModeNotAllowed => "Exclusive mode dimatikan di pengaturan perangkat Windows.",
        _ => $"Error WASAPI 0x{ex.HResult:X8}: {ex.Message}",
    };
}

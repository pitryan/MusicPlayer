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
        var (candidates, reason) = GetCandidates(source);
        if (reason is not null)
            return (null, reason);

        using var client = device.CreateAudioClient();

        foreach (var candidate in candidates)
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

        return (null, $"The device doesn't support {source.SampleRate / 1000.0:0.#} kHz / {source.Channels} ch in exclusive mode.");
    }

    /// <summary>
    /// Bagian negosiasi yang tidak butuh perangkat: format yang akan dicoba untuk sumber ini, atau alasan kenapa
    /// sumber ini sama sekali tidak bisa dikirim dalam exclusive mode (daftarnya kosong).
    /// Urutan prioritas: format asli lebih dulu, lalu wadah yang lebih besar.
    /// Wadah yang lebih kecil (misalnya 24 → 16-bit) tidak pernah dicoba karena membuang data.
    /// </summary>
    public static (IReadOnlyList<PcmFormat> Candidates, string? Reason) GetCandidates(WaveFormat source)
    {
        int bits = source.BitsPerSample;
        if (source.Encoding != WaveFormatEncoding.Pcm || bits is not (16 or 24 or 32))
            return ([], $"The decoded format ({source.Encoding}, {bits}-bit) isn't supported in exclusive mode yet.");

        // Di exclusive mode, perangkat seperti receiver HDMI memetakan channel ke speaker lewat mask ini. Tanpa
        // tata letak standar, channel bisa keluar dari speaker yang salah, jadi biarkan mixer Windows yang mengurus.
        if (PcmFormat.GetChannelMask(source) is not { } mask)
            return ([], $"{source.Channels}-channel audio has no standard speaker layout for exclusive mode.");

        var candidates = new List<PcmFormat>();
        void Add(int containerBits, int validBits) =>
            candidates.Add(new PcmFormat(source.SampleRate, source.Channels, containerBits, validBits) { ChannelMask = mask });

        if (bits is 16 or 24) Add(bits, bits);
        if (bits == 16) Add(24, 24);
        if (bits <= 24) Add(32, 24);
        Add(32, 32);
        return (candidates, null);
    }

    public static string DescribeError(COMException ex) => ex.HResult switch
    {
        // DeviceInUse juga muncul kalau aplikasi lain sedang memakai perangkat di shared mode dan opsi
        // "Give exclusive mode applications priority" di Windows dimatikan, jadi pesannya tidak menyebut mode
        AudioClientErrorCode.DeviceInUse => "Another app is using the device.",
        AudioClientErrorCode.ExclusiveModeNotAllowed => "Exclusive mode is turned off in the Windows sound settings for this device.",
        _ => $"WASAPI error 0x{ex.HResult:X8}: {ex.Message}",
    };
}

using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;

namespace MusicPlayer.Audio;

/// <summary>
/// Membedakan masalah perangkat audio (DAC dicabut, dipakai aplikasi lain, tidak ada output sama sekali) dari masalah
/// file lagu (file hilang, dikunci, rusak, atau tidak bisa di-decode). Yang pertama perlu menunggu perangkatnya; yang
/// kedua hanya mengenai lagu itu, jadi antrean bisa lanjut.
/// </summary>
/// <remarks>
/// Di NAudio 3.1 jenis exception-nya sudah menunjukkan asalnya: semua panggilan Core Audio (MMDeviceEnumerator,
/// MMDevice, AudioClient, AudioRenderClient) gagal sebagai <see cref="CoreAudioException"/>, dan semua panggilan
/// Media Foundation (membuka dan membaca file) sebagai <see cref="MediaFoundationException"/>. Keduanya turunan
/// <see cref="COMException"/>, jadi jenisnya harus dicek lebih dulu: MediaFoundationException juga membawa HRESULT
/// Win32 biasa, misalnya 0x80070002 kalau file hilang atau 0x8003001E kalau file gagal dibaca di tengah lagu.
/// </remarks>
public static class AudioErrors
{
    // ERROR_NOT_FOUND: Windows tidak punya perangkat output sama sekali, atau perangkatnya sudah hilang
    private const int NotFound = unchecked((int)0x80070490);

    // Facility AUDCLNT (severity error + facility 0x889): semua error WASAPI, misalnya AUDCLNT_E_DEVICE_INVALIDATED
    private const uint AudioClientErrorPrefix = 0x8889;

    /// <summary>
    /// True kalau <paramref name="ex"/> masalah perangkat audio atau output-nya. False untuk masalah file atau
    /// decoding, termasuk yang dilempar MediaFoundationReader.Read dari thread audio, dan untuk exception lain.
    /// Exception yang dibungkus dinilai dari exception terdalamnya.
    /// </summary>
    public static bool IsDeviceError(Exception ex) => ex.GetBaseException() switch
    {
        MediaFoundationException => false,
        CoreAudioException => true,
        COMException com => (uint)com.HResult >> 16 == AudioClientErrorPrefix || com.HResult == NotFound,

        // Sisanya bukan dari perangkat: FileNotFoundException, UnauthorizedAccessException, IOException, dan
        // InvalidOperationException "MediaFoundationReadError" dari MediaFoundationReader.Read
        _ => false,
    };
}

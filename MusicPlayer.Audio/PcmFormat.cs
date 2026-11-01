using System.Numerics;
using NAudio.Wave;

namespace MusicPlayer.Audio;

/// <summary>
/// Format PCM integer yang dikirim ke perangkat.
/// ContainerBits = ukuran "wadah" per sampel, ValidBits = bit yang benar-benar berisi audio.
/// Contoh: banyak DAC USB menerima audio 24-bit di dalam wadah 32-bit (ValidBits 24, ContainerBits 32).
/// </summary>
public sealed record PcmFormat(int SampleRate, int Channels, int ContainerBits, int ValidBits)
{
    /// <summary>
    /// Speaker untuk setiap channel, berurutan dari bit terendah. Bawaannya tata letak standar untuk jumlah channel
    /// ini (lihat <see cref="GetChannelMask"/>); None kalau jumlah channel-nya tidak punya tata letak standar.
    /// </summary>
    public Speakers ChannelMask { get; init; } = GetStandardChannelMask(Channels) ?? Speakers.None;

    public WaveFormat ToWaveFormat() =>
        new WaveFormatExtensible(SampleRate, ContainerBits, Channels,
            useIeeeFloat: false,
            validBitsPerSample: ValidBits,
            channelMask: ChannelMask);

    public override string ToString()
    {
        string bits = ContainerBits == ValidBits
            ? $"{ValidBits}-bit"
            : $"{ValidBits}-bit in a {ContainerBits}-bit container";
        return $"{SampleRate / 1000.0:0.#} kHz / {bits} / {Channels} ch";
    }

    /// <summary>
    /// Speaker mask untuk sumber ini. Mask dari decoder dipakai kalau ada dan cocok dengan jumlah channel-nya.
    /// MediaFoundationReader tidak pernah memberikannya (WaveFormat-nya selalu WaveFormat biasa, mask dari decoder
    /// dibuang), jadi biasanya yang dipakai tata letak standar Windows. Null kalau tidak ada tata letak standar.
    /// </summary>
    public static Speakers? GetChannelMask(WaveFormat source)
    {
        if (source is WaveFormatExtensible { ChannelMask: not 0 } extensible
            && BitOperations.PopCount((uint)extensible.ChannelMask) == source.Channels)
            return (Speakers)extensible.ChannelMask;

        return GetStandardChannelMask(source.Channels);
    }

    /// <summary>
    /// Tata letak standar (KSAUDIO_SPEAKER_*) untuk jumlah channel tertentu. Urutannya sama dengan urutan channel
    /// di FLAC dan WAV, jadi tidak ada channel yang perlu ditukar.
    /// </summary>
    private static Speakers? GetStandardChannelMask(int channels) => channels switch
    {
        1 => Speakers.FrontCenter,
        2 => Speakers.FrontLeft | Speakers.FrontRight,
        3 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter,
        4 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.BackLeft | Speakers.BackRight, // quad, 0x33
        5 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter
             | Speakers.BackLeft | Speakers.BackRight, // 5.0, 0x37
        6 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.LowFrequency
             | Speakers.BackLeft | Speakers.BackRight, // 5.1, 0x3F
        7 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.LowFrequency
             | Speakers.BackCenter | Speakers.SideLeft | Speakers.SideRight, // 6.1, 0x70F
        8 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.LowFrequency
             | Speakers.BackLeft | Speakers.BackRight | Speakers.SideLeft | Speakers.SideRight, // 7.1, 0x63F
        _ => null,
    };
}

using NAudio.Wave;

namespace MusicPlayer.AudioLab;

/// <summary>
/// Format PCM integer yang dikirim ke perangkat.
/// ContainerBits = ukuran "wadah" per sampel, ValidBits = bit yang benar-benar berisi audio.
/// Contoh: banyak DAC USB menerima audio 24-bit di dalam wadah 32-bit (ValidBits 24, ContainerBits 32).
/// </summary>
public sealed record PcmFormat(int SampleRate, int Channels, int ContainerBits, int ValidBits)
{
    public WaveFormat ToWaveFormat() =>
        new WaveFormatExtensible(SampleRate, ContainerBits, Channels,
            useIeeeFloat: false,
            validBitsPerSample: ValidBits,
            channelMask: GetChannelMask(Channels));

    public override string ToString()
    {
        string bits = ContainerBits == ValidBits
            ? $"{ValidBits}-bit"
            : $"{ValidBits}-bit dalam wadah {ContainerBits}-bit";
        return $"{SampleRate / 1000.0:0.#} kHz / {bits} / {Channels} ch";
    }

    private static Speakers GetChannelMask(int channels) => channels switch
    {
        1 => Speakers.FrontCenter,
        2 => Speakers.FrontLeft | Speakers.FrontRight,
        _ => (Speakers)((1 << channels) - 1),
    };
}

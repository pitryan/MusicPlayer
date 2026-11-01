using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// Speaker mask yang dikirim ke perangkat dalam exclusive mode. Semua tes di sini murni, tanpa perangkat audio.
/// </summary>
public class ChannelMaskTests
{
    // Tata letak standar Windows (KSAUDIO_SPEAKER_*), sama dengan urutan channel di FLAC dan WAV
    public static TheoryData<int, int> StandardMasks => new()
    {
        { 1, 0x4 },   // mono: FC
        { 2, 0x3 },   // stereo: FL FR
        { 3, 0x7 },   // FL FR FC
        { 4, 0x33 },  // quad: FL FR BL BR
        { 5, 0x37 },  // 5.0: FL FR FC BL BR
        { 6, 0x3F },  // 5.1: FL FR FC LFE BL BR
        { 7, 0x70F }, // 6.1: FL FR FC LFE BC SL SR
        { 8, 0x63F }, // 7.1: FL FR FC LFE BL BR SL SR
    };

    [Theory]
    [MemberData(nameof(StandardMasks))]
    public void ToWaveFormat_UsesTheStandardSpeakerLayout(int channels, int expectedMask)
    {
        var format = new PcmFormat(44100, channels, 32, 24).ToWaveFormat();

        var extensible = Assert.IsType<WaveFormatExtensible>(format);
        Assert.Equal(expectedMask, extensible.ChannelMask);
        Assert.Equal(channels, extensible.Channels);
    }

    [Theory]
    [MemberData(nameof(StandardMasks))]
    public void GetCandidates_EveryCandidateCarriesTheStandardMask(int channels, int expectedMask)
    {
        var (candidates, reason) = ExclusiveFormatNegotiator.GetCandidates(new WaveFormat(96000, 24, channels));

        Assert.Null(reason);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal(expectedMask,
            Assert.IsType<WaveFormatExtensible>(c.ToWaveFormat()).ChannelMask));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(16)]
    public void GetCandidates_ChannelCountWithoutStandardLayout_FallsBackWithReason(int channels)
    {
        var (candidates, reason) = ExclusiveFormatNegotiator.GetCandidates(new WaveFormat(48000, 16, channels));

        Assert.Empty(candidates);
        Assert.Equal($"{channels}-channel audio has no standard speaker layout for exclusive mode.", reason);
    }

    [Fact]
    public void GetChannelMask_PrefersTheDecodersOwnMask()
    {
        // 5.1 dengan speaker samping (0x60F), bukan speaker belakang seperti tata letak standar
        var source = new WaveFormatExtensible(48000, 24, 6, useIeeeFloat: false, validBitsPerSample: 24,
            channelMask: Speakers.Surround51);

        Assert.Equal(Speakers.Surround51, PcmFormat.GetChannelMask(source));
    }

    [Fact]
    public void GetChannelMask_IgnoresADecoderMaskThatDoesNotMatchTheChannelCount()
    {
        // Mask stereo untuk sumber 6 channel tidak masuk akal: pakai tata letak standar
        var source = new WaveFormatExtensible(48000, 24, 6, useIeeeFloat: false, validBitsPerSample: 24,
            channelMask: Speakers.Stereo);

        Assert.Equal((Speakers)0x3F, PcmFormat.GetChannelMask(source));
    }

    [Fact]
    public void GetCandidates_KeepsTheContainerOrder()
    {
        var (candidates, _) = ExclusiveFormatNegotiator.GetCandidates(new WaveFormat(44100, 16, 2));

        Assert.Equal([(16, 16), (24, 24), (32, 24), (32, 32)], candidates.Select(c => (c.ContainerBits, c.ValidBits)));
    }

    [Fact]
    public void GetCandidates_FloatSource_FallsBackWithReason()
    {
        var (candidates, reason) = ExclusiveFormatNegotiator.GetCandidates(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));

        Assert.Empty(candidates);
        Assert.Equal("The decoded format (IeeeFloat, 32-bit) isn't supported in exclusive mode yet.", reason);
    }

    [Fact]
    public void MediaFoundationReader_NeverReportsAChannelMask()
    {
        // File 5.1 dengan speaker samping (0x60F) di header-nya. MediaFoundationReader membuang mask itu, jadi
        // GetChannelMask memakai tata letak standar untuk 6 channel
        using var folder = new TempAudioFolder();
        string path = folder.CreateWav("side51.wav", TimeSpan.FromMilliseconds(200),
            new WaveFormatExtensible(44100, 16, 6, useIeeeFloat: false, validBitsPerSample: 16, channelMask: Speakers.Surround51));

        using var reader = new MediaFoundationReader(path);

        Assert.IsNotType<WaveFormatExtensible>(reader.WaveFormat);
        Assert.Equal(6, reader.WaveFormat.Channels);
        Assert.Equal((Speakers)0x3F, PcmFormat.GetChannelMask(reader.WaveFormat));
    }
}

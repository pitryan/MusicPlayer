using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// Perilaku PcmOutputProvider di akhir lagu, yang dipakai AudioEngine untuk memutuskan kapan player dihentikan.
/// Read dipanggil langsung oleh tes, seperti yang dilakukan WasapiPlayer dari thread audio; tidak ada perangkat yang dibuka.
/// </summary>
public sealed class PcmOutputProviderTests : IDisposable
{
    private readonly TempAudioFolder _folder = new();
    private readonly MediaFoundationReader _reader;

    public PcmOutputProviderTests()
    {
        _reader = new MediaFoundationReader(_folder.CreateWav("song.wav", TimeSpan.FromMilliseconds(300)));
    }

    public void Dispose()
    {
        _reader.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void PausedAfterTheEnd_StaysEndedAndPaused_AndSendsOnlySilence()
    {
        var output = NewOutput(out var ends);
        ReadToEnd(output);

        // Pengguna menekan pause selama sisa audio di buffer perangkat masih diputar
        output.IsPaused = true;
        var buffer = Filled(output.WaveFormat.AverageBytesPerSecond / 10);
        int read = output.Read(buffer);

        Assert.True(output.HasEnded);
        Assert.True(output.IsPaused);
        Assert.Equal(buffer.Length, read);
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Single(ends);
    }

    [Fact]
    public void AfterTheEnd_ReadingAgain_DoesNotRaiseEndOfStreamAgain()
    {
        // Karena itu AudioEngine.Play sendiri yang harus menjadwalkan stop untuk lagu yang sudah habis
        var output = NewOutput(out var ends);
        ReadToEnd(output);

        output.IsPaused = false;
        for (int i = 0; i < 5; i++)
            output.Read(new byte[4096]);

        Assert.True(output.HasEnded);
        Assert.Single(ends);
    }

    [Fact]
    public void NewProviderOnAReaderAtTheEnd_RaisesEndOfStreamOnItsFirstRead()
    {
        // Lagu di-pause di akhir lalu perangkatnya dilepas. Saat Play, ReopenDevice membuat provider baru dari reader
        // yang sama, yang posisinya sudah di akhir. Provider itu harus langsung habis, supaya lagu selesai dan antrean
        // maju seperti biasa (bukan diam selamanya).
        ReadToEnd(NewOutput(out _));
        var reopened = NewOutput(out var ends);

        Assert.False(reopened.HasEnded);
        var buffer = Filled(4096);
        int read = reopened.Read(buffer);

        Assert.True(reopened.HasEnded);
        Assert.Single(ends);
        Assert.Equal(buffer.Length, read); // hening, bukan 0 (lihat PcmOutputProvider.EndOfStream)
        Assert.All(buffer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void SeekAfterTheEnd_ClearsTheEndedState()
    {
        var output = NewOutput(out var ends);
        ReadToEnd(output);

        output.CurrentTime = TimeSpan.FromMilliseconds(100);

        Assert.False(output.HasEnded);
        ReadToEnd(output);
        Assert.Equal(2, ends.Count);
    }

    private PcmOutputProvider NewOutput(out List<object?> ends)
    {
        var raised = new List<object?>();
        var output = new PcmOutputProvider(_reader, _reader.WaveFormat);
        output.EndOfStream += (sender, _) => raised.Add(sender);
        ends = raised;
        return output;
    }

    private static void ReadToEnd(PcmOutputProvider output)
    {
        var buffer = new byte[output.WaveFormat.BlockAlign * 441];
        for (int i = 0; i < 1000 && !output.HasEnded; i++)
            output.Read(buffer);
        Assert.True(output.HasEnded);
    }

    // Buffer yang tidak kosong, supaya kelihatan apakah Read benar-benar menulis hening ke dalamnya
    private static byte[] Filled(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, (byte)0x5A);
        return buffer;
    }
}

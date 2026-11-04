using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// Seek di tengah pemutaran. Media Foundation tidak bisa seek dengan tepat di reader yang sedang dibaca untuk FLAC tanpa
/// SEEKTABLE, jadi AudioEngine memasang reader baru yang sudah berada di posisi tujuan (lihat AudioEngine.SeekAsync).
/// </summary>
public sealed class SeekTests : IDisposable
{
    // 40 detik mono 8 kHz tanpa SEEKTABLE: 20 detik hening lalu 20 detik derau, supaya perkiraan posisi dari ukuran file
    // meleset jauh. Seek cara lama (CurrentTime di reader yang sedang dibaca) mendarat di 24,8 s saat diminta 10 s. Dibuat
    // dengan: ffmpeg -f lavfi -i "aevalsrc='if(lt(t,20),0,0.01*(2*random(0)-1))':s=8000:d=40" -ac 1 -sample_fmt s16 -c:a flac
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "seek-silence-then-noise.flac");
    private static readonly TimeSpan FixtureLength = TimeSpan.FromSeconds(40);

    private readonly TempAudioFolder _folder = new();
    private readonly AudioEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void NewReaderInstalledDuringPlayback_ContinuesExactlyFromTheTarget()
    {
        using var reader = AudioEngine.OpenReader(Fixture, TimeSpan.Zero);
        var output = new PcmOutputProvider(reader, reader.WaveFormat);
        Play(output, TimeSpan.FromSeconds(5));

        // Seperti AudioEngine.SeekAsync
        output.BeginSeek();
        using var positioned = AudioEngine.OpenReader(Fixture, TimeSpan.FromSeconds(10));
        output.EndSeek(positioned);

        Assert.Equal(TimeSpan.FromSeconds(10), output.CurrentTime);
        Assert.Equal(30.0, PlayToEnd(output).TotalSeconds, precision: 2);
        Assert.Equal(FixtureLength.TotalSeconds, output.CurrentTime.TotalSeconds, precision: 2);
    }

    [Fact]
    public void WhileSeeking_OnlySilenceIsSent_AndTheSongDoesNotAdvance()
    {
        using var reader = AudioEngine.OpenReader(Fixture, TimeSpan.FromSeconds(30));
        var output = new PcmOutputProvider(reader, reader.WaveFormat);

        output.BeginSeek();
        var buffer = new byte[output.WaveFormat.AverageBytesPerSecond];
        Array.Fill(buffer, (byte)0x5A);
        int read = output.Read(buffer);

        Assert.Equal(buffer.Length, read);
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(TimeSpan.FromSeconds(30), output.CurrentTime);
    }

    [Fact]
    public void SeekAfterTheEnd_TheSongIsNoLongerEnded_EvenBeforeTheNewReaderIsReady()
    {
        // Lagu habis, lalu pengguna seek mundur sebelum AudioEngine sempat menghentikan player: lagu tidak boleh dianggap
        // selesai (antrean maju) selama reader barunya disiapkan
        using var reader = AudioEngine.OpenReader(Fixture, TimeSpan.FromSeconds(39));
        var output = new PcmOutputProvider(reader, reader.WaveFormat);
        int ends = 0;
        output.EndOfStream += (_, _) => ends++;
        PlayToEnd(output);
        Assert.True(output.HasEnded);

        output.BeginSeek();
        output.Read(new byte[4096]);

        Assert.False(output.HasEnded);
        Assert.Equal(1, ends);
    }

    [Fact]
    public async Task SeekAsync_ShowsTheTargetRightAway_AndKeepsItOnceTheReaderIsReady()
    {
        _engine.OpenWithoutDevice(Fixture);

        var seek = _engine.SeekAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(TimeSpan.FromSeconds(25), _engine.Position);

        await seek;
        Assert.Equal(TimeSpan.FromSeconds(25), _engine.Position);
        Assert.True(_engine.IsOpen);
    }

    [Fact]
    public async Task SeekAsync_SongClosedBeforeTheReaderIsReady_StaysClosed()
    {
        _engine.OpenWithoutDevice(Fixture);

        var seek = _engine.SeekAsync(TimeSpan.FromSeconds(25));
        _engine.Close();
        await seek;

        Assert.False(_engine.IsOpen);
        Assert.Equal(TimeSpan.Zero, _engine.Position);
    }

    [Fact]
    public async Task SeekAsync_FileGoneMeanwhile_KeepsTheSongWhereItWas()
    {
        // Misalnya file-nya dipindah selama lagu terbuka: reader baru tidak bisa dibuat, dan seek tidak boleh gagal
        string path = _folder.CreateWav("song.wav", TimeSpan.FromSeconds(2));
        _engine.OpenWithoutDevice(path);
        File.Move(path, _folder.File("moved.wav"));

        await _engine.SeekAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, _engine.Position);
        Assert.True(_engine.IsOpen);
    }

    // Seperti WasapiPlayer: Read dipanggil berulang dengan buffer kecil
    private static void Play(PcmOutputProvider output, TimeSpan duration)
    {
        var buffer = new byte[output.WaveFormat.AverageBytesPerSecond / 25];
        long wanted = (long)(duration.TotalSeconds * output.WaveFormat.AverageBytesPerSecond);
        for (long done = 0; done < wanted; done += output.Read(buffer))
        {
        }
    }

    /// <summary>Membaca sampai lagu habis; mengembalikan panjang audio yang benar-benar terbaca (tanpa hening sesudahnya).</summary>
    private static TimeSpan PlayToEnd(PcmOutputProvider output)
    {
        var buffer = new byte[output.WaveFormat.AverageBytesPerSecond / 25];
        var before = output.CurrentTime;
        for (int i = 0; i < 100_000 && !output.HasEnded; i++)
            output.Read(buffer);
        Assert.True(output.HasEnded);
        return output.CurrentTime - before;
    }
}

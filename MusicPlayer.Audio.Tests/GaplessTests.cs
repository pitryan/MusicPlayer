using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// Gapless: begitu data lagu sekarang habis, lagu berikutnya menyambung di buffer yang sama, tanpa jeda dan tanpa sampel yang
/// hilang atau berubah (lihat PcmOutputProvider.ReplaceNext dan AudioEngine.PrepareNextAsync). Read dipanggil langsung oleh tes,
/// seperti yang dilakukan WasapiPlayer dari thread audio; tidak ada perangkat yang dibuka.
/// </summary>
public sealed class GaplessTests : IDisposable
{
    // FLAC tanpa SEEKTABLE yang juga dipakai SeekTests: 40 detik mono 8 kHz
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "seek-silence-then-noise.flac");
    private static readonly WaveFormat Cd = new(44100, 16, 2);

    private readonly TempAudioFolder _folder = new();

    // "UI thread" engine ini: NextStarted baru dipicu saat tes menjalankan _ui.RunPosted(), seperti event yang masih antre
    private readonly ManualContext _ui = new();
    private readonly AudioEngine _engine;

    public GaplessTests()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_ui);
        try
        {
            _engine = new AudioEngine();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Dispose();
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)] // 16-bit dikirim dalam wadah 24-bit: konversinya ikut menyambung
    public void NextSong_ContinuesInTheSameBuffer_WithoutAGapOrLostSamples(int outputBits)
    {
        byte[] first = Ramp(frames: 1234, start: 1);
        byte[] second = Ramp(frames: 777, start: 20000);
        var output = new PcmOutputProvider(Stream(first), new WaveFormat(44100, outputBits, 2));
        var events = new List<string>();
        output.NextStarted += (_, _) => events.Add("next");
        output.EndOfStream += (_, _) => events.Add("end");

        Assert.True(output.ReplaceNext(null, Stream(second)));
        byte[] played = ReadUntilEnded(output, framesPerRead: 100); // tidak pas dengan panjang kedua lagu

        byte[] expected = Widen([.. first, .. second], outputBits / 8);
        Assert.Equal(expected, played[..expected.Length]);
        Assert.All(played[expected.Length..], b => Assert.Equal(0, b)); // setelah lagu kedua habis: hening
        Assert.Equal(["next", "end"], events);
    }

    [Fact]
    public void VeryShortNextSong_StartsAndEndsInOneRead_InThatOrder()
    {
        // AudioEngine harus tahu lagu itu sudah mulai sebelum lagu itu dianggap selesai
        var output = new PcmOutputProvider(Stream(Ramp(50, 1)), Cd);
        var events = new List<string>();
        output.NextStarted += (_, _) => events.Add("next");
        output.EndOfStream += (_, _) => events.Add("end");
        output.ReplaceNext(null, Stream(Ramp(20, 100)));

        ReadFull(output, new byte[200 * Cd.BlockAlign]);

        Assert.Equal(["next", "end"], events);
    }

    [Fact]
    public void FlacFollowedByFlac_IsSampleExact()
    {
        // Lewat Media Foundation, seperti di aplikasi: lagu kedua menyambung tepat setelah sampel terakhir lagu pertama
        byte[] once = Decode(Fixture);
        using var first = AudioEngine.OpenReader(Fixture, TimeSpan.Zero);
        using var second = AudioEngine.OpenReader(Fixture, TimeSpan.Zero);
        var output = new PcmOutputProvider(first, first.WaveFormat);

        Assert.True(output.ReplaceNext(null, second));
        byte[] played = ReadUntilEnded(output, framesPerRead: 8000 / 25);

        Assert.Equal(once, played[..once.Length]);
        Assert.Equal(once, played[once.Length..(2 * once.Length)]);
        Assert.All(played[(2 * once.Length)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void NextSongWithAnotherFormat_IsRefused()
    {
        // Sampelnya tidak pernah diubah, dan perangkat sudah dibuka untuk format lagu sekarang
        var output = new PcmOutputProvider(Stream(Ramp(100, 1)), Cd);

        Assert.Throws<ArgumentException>(() => output.ReplaceNext(null, Stream(new byte[400], new WaveFormat(48000, 16, 2))));
        Assert.Throws<ArgumentException>(() => output.ReplaceNext(null, Stream(new byte[600], new WaveFormat(44100, 24, 2))));
        Assert.Throws<ArgumentException>(() => output.ReplaceNext(null, Stream(new byte[200], new WaveFormat(44100, 16, 1))));
    }

    [Fact]
    public void NextSongCancelledBeforeTheEnd_TheSongEndsAsBefore()
    {
        byte[] first = Ramp(500, 1);
        var second = Stream(Ramp(500, 1000));
        var output = new PcmOutputProvider(Stream(first), Cd);
        var events = new List<string>();
        output.NextStarted += (_, _) => events.Add("next");
        output.EndOfStream += (_, _) => events.Add("end");

        Assert.True(output.ReplaceNext(null, second));
        Assert.True(output.ReplaceNext(second, null));
        byte[] played = ReadUntilEnded(output, framesPerRead: 100);

        Assert.Equal(first, played[..first.Length]);
        Assert.All(played[first.Length..], b => Assert.Equal(0, b));
        Assert.Equal(["end"], events);
    }

    [Fact]
    public void NextSongThatAlreadyStarted_CanNoLongerBeReplaced()
    {
        // AudioEngine.CancelNext tepat saat lagu berikutnya mulai di thread audio: lagu itu tetap berbunyi
        var second = Stream(Ramp(5000, 1000));
        var output = new PcmOutputProvider(Stream(Ramp(500, 1)), Cd);
        output.ReplaceNext(null, second);

        output.Read(new byte[1000 * Cd.BlockAlign]);

        Assert.False(output.ReplaceNext(second, null));
        Assert.Same(second, output.Source);
        Assert.False(output.HasEnded);
    }

    [Fact]
    public async Task PrepareNextAsync_SameFormat_TheNextSongBecomesTheOpenSongOnceItStarts()
    {
        var (_, second) = await StartSecondSongAsync();
        var announced = new List<string>();
        _engine.NextStarted += (_, path) => announced.Add(path);

        _ui.RunPosted();

        Assert.Equal([second], announced);
        Assert.Null(_engine.NextPath);
        var output = _engine.Output!;
        Assert.Equal(2.0, _engine.Duration.TotalSeconds, precision: 2);
        Assert.Equal(0.1, _engine.Position.TotalSeconds, precision: 2);
        Assert.False(output.HasEnded);

        // Seek sesudahnya membuka lagu kedua, bukan lagu pertama yang sudah selesai
        await _engine.SeekAsync(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2.0, _engine.Duration.TotalSeconds, precision: 2);
        Assert.Equal(1.5, _engine.Position.TotalSeconds, precision: 2);
    }

    [Fact]
    public async Task SeekRightAfterTheNextSongStarted_BeforeItIsAnnounced_SeeksInTheNextSong()
    {
        // Lagu berganti di thread audio, dan pengguna seek sebelum NextStarted sampai di UI thread
        var (_, second) = await StartSecondSongAsync();
        var announced = new List<string>();
        _engine.NextStarted += (_, path) => announced.Add(path);

        await _engine.SeekAsync(TimeSpan.FromSeconds(1.5));

        Assert.Equal(2.0, _engine.Duration.TotalSeconds, precision: 2);
        Assert.Equal(1.5, _engine.Position.TotalSeconds, precision: 2);
        Assert.Empty(announced);
        _ui.RunPosted();
        Assert.Equal([second], announced); // tetap diumumkan, sekali
    }

    [Fact]
    public async Task ChangingTheNextSongRightAfterItStarted_KeepsItPlaying_AndPreparesNothingYet()
    {
        // Antrean berubah tepat saat lagu berganti: lagu yang sudah berbunyi tidak bisa dibatalkan, dan lagu sesudahnya baru
        // dipilih setelah NextStarted (pilihan pemanggil masih untuk lagu sebelumnya)
        var (first, second) = await StartSecondSongAsync();
        var announced = new List<string>();
        _engine.NextStarted += (_, path) => announced.Add(path);

        Assert.False(await _engine.PrepareNextAsync(first));

        Assert.Null(_engine.NextPath);
        Assert.Equal(2.0, _engine.Duration.TotalSeconds, precision: 2);
        _ui.RunPosted();
        Assert.Equal([second], announced);
        Assert.True(await _engine.PrepareNextAsync(first));
    }

    [Fact]
    public async Task SongClosedRightAfterTheNextSongStarted_NothingIsAnnounced()
    {
        // Misalnya pengguna menekan Next tepat saat lagu berganti: lagu yang dibuka sesudahnya yang berlaku
        var (_, second) = await StartSecondSongAsync();
        var announced = new List<string>();
        _engine.NextStarted += (_, path) => announced.Add(path);

        _engine.CancelNext(); // lagu kedua sudah menjadi lagu yang terbuka, dan menunggu diumumkan
        _engine.Close();
        _ui.RunPosted();

        Assert.Empty(announced);
        OpenExclusively(second);
    }

    [Fact]
    public async Task PrepareNextAsync_AnotherSampleRate_DoesNotContinue_AndIsNotTriedAgain()
    {
        // Perangkat harus dibuka ulang untuk 48 kHz: lagu pertama berhenti seperti biasa, dan lagu itu diputar sesudahnya
        string first = _folder.CreateWav("first.wav", TimeSpan.FromMilliseconds(300));
        string other = _folder.CreateWav("other.wav", TimeSpan.FromSeconds(1), new WaveFormat(48000, 16, 2));
        _engine.OpenWithoutDevice(first);
        var output = _engine.Output!;
        int ends = 0;
        output.EndOfStream += (_, _) => ends++;

        Assert.False(await _engine.PrepareNextAsync(other));
        Assert.Equal(other, _engine.NextPath);

        output.IsPaused = false;
        var buffer = new byte[output.WaveFormat.AverageBytesPerSecond / 10];
        for (int i = 0; i < 4; i++)
            output.Read(buffer);

        Assert.True(output.HasEnded);
        Assert.Equal(1, ends);
    }

    [Fact]
    public async Task PrepareNextAsync_FileThatCantBeOpened_IsNotPrepared()
    {
        _engine.OpenWithoutDevice(_folder.CreateWav("first.wav", TimeSpan.FromMilliseconds(300)));
        string missing = _folder.File("missing.wav");

        Assert.False(await _engine.PrepareNextAsync(missing));
        Assert.Equal(missing, _engine.NextPath); // error-nya nanti tampil saat lagu itu diputar seperti biasa
    }

    [Fact]
    public async Task CancelNext_ReleasesTheFile_AndTheSongEndsAsUsual()
    {
        // File lagu berikutnya harus bisa ditulis lagi (edit tag) setelah dibatalkan
        string first = _folder.CreateWav("first.wav", TimeSpan.FromMilliseconds(300));
        string second = _folder.CreateWav("second.wav", TimeSpan.FromSeconds(1));
        _engine.OpenWithoutDevice(first);
        var output = _engine.Output!;
        int ends = 0;
        output.EndOfStream += (_, _) => ends++;

        Assert.True(await _engine.PrepareNextAsync(second));
        Assert.ThrowsAny<IOException>(() => OpenExclusively(second));

        _engine.CancelNext();

        Assert.Null(_engine.NextPath);
        OpenExclusively(second);
        output.IsPaused = false;
        output.Read(new byte[output.WaveFormat.AverageBytesPerSecond / 2]);
        Assert.True(output.HasEnded);
        Assert.Equal(1, ends);
    }

    [Fact]
    public async Task Close_ReleasesTheNextSong()
    {
        string second = _folder.CreateWav("second.wav", TimeSpan.FromSeconds(1));
        _engine.OpenWithoutDevice(_folder.CreateWav("first.wav", TimeSpan.FromMilliseconds(300)));
        Assert.True(await _engine.PrepareNextAsync(second));

        _engine.Close();

        Assert.Null(_engine.NextPath);
        OpenExclusively(second);
    }

    [Fact]
    public async Task PrepareNextAsync_SongClosedBeforeTheFileIsOpen_PreparesNothing()
    {
        string second = _folder.CreateWav("second.wav", TimeSpan.FromSeconds(1));
        _engine.OpenWithoutDevice(_folder.CreateWav("first.wav", TimeSpan.FromMilliseconds(300)));

        var prepare = _engine.PrepareNextAsync(second);
        _engine.Close();

        Assert.False(await prepare);
        Assert.Null(_engine.NextPath);
        OpenExclusively(second);
    }

    /// <summary>
    /// Lagu pertama (300 ms) dengan lagu kedua (2 detik) yang disiapkan, lalu 400 ms dibaca: lagu kedua sudah berbunyi 100 ms di
    /// thread audio, tapi NextStarted masih antre di "UI thread".
    /// </summary>
    private async Task<(string First, string Second)> StartSecondSongAsync()
    {
        string first = _folder.CreateWav("first.wav", TimeSpan.FromMilliseconds(300));
        string second = _folder.CreateWav("second.wav", TimeSpan.FromSeconds(2));
        _engine.OpenWithoutDevice(first);
        Assert.True(await _engine.PrepareNextAsync(second));
        Assert.Equal(second, _engine.NextPath);

        var output = _engine.Output!;
        output.IsPaused = false;
        var buffer = new byte[output.WaveFormat.AverageBytesPerSecond / 10];
        for (int i = 0; i < 4; i++)
            output.Read(buffer);
        Assert.Equal(1, _ui.Pending);
        return (first, second);
    }

    private static void OpenExclusively(string path)
    {
        using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Antrean UI thread yang dijalankan tes sendiri, supaya urutan kejadiannya pasti.</summary>
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public int Pending
        {
            get
            {
                lock (_posted) return _posted.Count;
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posted) _posted.Enqueue((d, state));
        }

        public void RunPosted()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_posted)
                {
                    if (!_posted.TryDequeue(out item))
                        return;
                }
                item.Callback(item.State);
            }
        }
    }

    /// <summary>
    /// Membaca seperti WasapiPlayer sampai lagu terakhir habis, lalu satu buffer lagi; semua yang dikirim ke perangkat,
    /// termasuk hening sesudahnya.
    /// </summary>
    private static byte[] ReadUntilEnded(PcmOutputProvider output, int framesPerRead)
    {
        var played = new List<byte>();
        var buffer = new byte[framesPerRead * output.WaveFormat.BlockAlign];
        for (int i = 0; i < 100_000 && !output.HasEnded; i++)
            played.AddRange(ReadFull(output, buffer));
        Assert.True(output.HasEnded);
        played.AddRange(ReadFull(output, buffer));
        return [.. played];
    }

    // Exclusive mode mewajibkan buffer penuh di setiap bacaan (lihat PcmOutputProvider.EndOfStream), juga saat lagu berganti
    private static byte[] ReadFull(PcmOutputProvider output, byte[] buffer)
    {
        Assert.Equal(buffer.Length, output.Read(buffer));
        return buffer.ToArray();
    }

    /// <summary>Isi sebuah file setelah di-decode, lewat reader biasa.</summary>
    private static byte[] Decode(string path)
    {
        using var reader = AudioEngine.OpenReader(path, TimeSpan.Zero);
        using var data = new MemoryStream();
        reader.CopyTo(data);
        return data.ToArray();
    }

    /// <summary>Audio 16-bit stereo yang setiap sampelnya berbeda, supaya sampel yang hilang, ganda, atau tergeser ketahuan.</summary>
    private static byte[] Ramp(int frames, int start)
    {
        var data = new byte[frames * Cd.BlockAlign];
        for (int i = 0; i < frames; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 4), (short)(start + i));
            BitConverter.TryWriteBytes(data.AsSpan(i * 4 + 2), (short)-(start + i));
        }
        return data;
    }

    private static RawSourceWaveStream Stream(byte[] data, WaveFormat? format = null) => new(new MemoryStream(data), format ?? Cd);

    /// <summary>Sampel 16-bit di wadah yang lebih besar, seperti PcmOutputProvider: rata kiri, bit bawahnya nol.</summary>
    private static byte[] Widen(byte[] samples16, int containerBytes)
    {
        var result = new byte[samples16.Length / 2 * containerBytes];
        for (int i = 0; i < samples16.Length / 2; i++)
        {
            result[i * containerBytes + containerBytes - 2] = samples16[i * 2];
            result[i * containerBytes + containerBytes - 1] = samples16[i * 2 + 1];
        }
        return result;
    }
}

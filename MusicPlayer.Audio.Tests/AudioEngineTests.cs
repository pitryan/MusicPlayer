using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// Bagian AudioEngine yang bisa dites tanpa perangkat audio: lagu dibuka dengan OpenWithoutDevice, yang hanya
/// memakai Media Foundation. Tidak ada WasapiPlayer yang dibuat, jadi tidak ada suara dan DAC tidak tersentuh.
/// </summary>
public sealed class AudioEngineTests : IDisposable
{
    private readonly TempAudioFolder _folder = new();
    private readonly AudioEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void IsOpen_IsFalseBeforeAnySongIsOpened()
    {
        Assert.False(_engine.IsOpen);
        Assert.Equal(PlaybackState.Stopped, _engine.State);
    }

    [Fact]
    public void IsOpen_IsTrueForASongOpenedWithoutDevice_UntilClose()
    {
        _engine.OpenWithoutDevice(_folder.CreateWav("song.wav", TimeSpan.FromMilliseconds(300)));

        Assert.True(_engine.IsOpen);
        Assert.True(_engine.IsDeviceReleased);
        Assert.Equal(PlaybackState.Paused, _engine.State);

        _engine.Close();

        Assert.False(_engine.IsOpen);
        Assert.False(_engine.IsDeviceReleased);
    }

    [Fact]
    public void IsOpen_IsFalseAfterAFailedOpen_EvenIfASongWasOpenBefore()
    {
        _engine.OpenWithoutDevice(_folder.CreateWav("song.wav", TimeSpan.FromMilliseconds(300)));

        Assert.ThrowsAny<Exception>(() => _engine.OpenWithoutDevice(_folder.File("missing.flac")));

        Assert.False(_engine.IsOpen);
        Assert.Equal(PlaybackState.Stopped, _engine.State);
    }

    [Fact]
    public void Play_WithoutDevice_DoesNothing()
    {
        // Pemanggil harus membuka perangkat lebih dulu dengan ReopenDevice (lihat PlaybackService.Resume)
        _engine.OpenWithoutDevice(_folder.CreateWav("song.wav", TimeSpan.FromMilliseconds(300)));
        _engine.Seek(TimeSpan.FromMilliseconds(200));

        _engine.Play();

        Assert.Equal(PlaybackState.Paused, _engine.State);
        Assert.Equal(TimeSpan.FromMilliseconds(200), _engine.Position);
    }
}

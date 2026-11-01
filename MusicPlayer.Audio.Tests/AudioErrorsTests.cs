using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MusicPlayer.Audio.Tests;

/// <summary>
/// AudioErrors.IsDeviceError dengan exception yang benar-benar dilempar NAudio. Error file dibuat sungguhan dengan
/// MediaFoundationReader (tanpa perangkat audio); error perangkat dibuat dengan tipe dan HRESULT yang sama dengan yang
/// dilempar NAudio 3.1, karena membuatnya sungguhan butuh perangkat.
/// </summary>
public sealed class AudioErrorsTests : IDisposable
{
    private readonly TempAudioFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // ── Error file yang benar-benar terjadi ─────────────────────────

    [Fact]
    public void MissingFile_IsNotADeviceError() =>
        AssertFileError(OpenReader(_folder.File("deleted.flac")), 0x80070002);

    [Fact]
    public void MissingFolder_IsNotADeviceError() =>
        AssertFileError(OpenReader(_folder.File(Path.Combine("gone", "song.flac"))), 0x80070003);

    [Fact]
    public void TextFileNamedFlac_IsNotADeviceError()
    {
        string path = _folder.File("not-audio.flac");
        File.WriteAllText(path, "this is not audio");

        AssertFileError(OpenReader(path), 0xC00D36C4); // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
    }

    [Fact]
    public void FileLockedByAnotherProgram_IsNotADeviceError()
    {
        string path = _folder.CreateWav("locked.wav", TimeSpan.FromMilliseconds(100));
        using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        AssertFileError(OpenReader(path), 0x80070020); // ERROR_SHARING_VIOLATION
    }

    [Fact]
    public void FileDeletedBeforePlaybackStarts_FailsInReadOnTheAudioThread_AndIsNotADeviceError()
    {
        // MediaFoundationReader baru membuka file untuk dibaca saat Read pertama, dan Read dipanggil WasapiPlayer dari
        // thread audio. Exception-nya diteruskan ke PlaybackStopped sebagai e.Exception, jadi harus dikenali sebagai
        // error file, bukan perangkat yang dicabut.
        string path = _folder.CreateWav("removed.wav", TimeSpan.FromMilliseconds(500));
        using var reader = new MediaFoundationReader(path);
        var output = new PcmOutputProvider(reader, reader.WaveFormat);
        File.Delete(path);

        var ex = Record.Exception(() => output.Read(new byte[4096]));

        AssertFileError(ex, 0x80070002);
    }

    // ── Error file yang dibuat dengan tipe dan HRESULT yang sama ─────

    public static TheoryData<Exception> FileErrors => new()
    {
        new COMException("File not found", unchecked((int)0x80070002)),
        new COMException("Path not found", unchecked((int)0x80070003)),
        new COMException("Access denied", unchecked((int)0x80070005)),
        new COMException("Sharing violation", unchecked((int)0x80070020)),
        new FileNotFoundException("gone", "song.flac"),
        new DirectoryNotFoundException("gone"),
        new UnauthorizedAccessException("denied"),
        new IOException("in use", unchecked((int)0x80070020)),

        // Dilempar MediaFoundationReader.Read: file gagal dibaca di tengah lagu (STG_E_READFAULT, dilihat sungguhan
        // dengan stream yang gagal di tengah), format rusak, dan flag error dari source reader
        new MediaFoundationException(unchecked((int)0x8003001E)),
        new MediaFoundationException(unchecked((int)0xC00D36E5)),
        new MediaFoundationException(unchecked((int)0x8007001F)),
        new InvalidOperationException("MediaFoundationReadError Error"),

        // MediaFoundationException dengan HRESULT yang mirip error perangkat tetap error file: asalnya dari decoder
        new MediaFoundationException(unchecked((int)0x80070490)),

        // Format yang tidak bisa dipakai perangkat tanpa resampling (WasapiPlayer.Init), dan exception biasa
        new NotSupportedException("cannot be adapted without resampling"),
        new InvalidOperationException("No song is open."),
    };

    [Theory]
    [MemberData(nameof(FileErrors))]
    public void FileAndDecodeErrors_AreNotDeviceErrors(Exception ex) => Assert.False(AudioErrors.IsDeviceError(ex));

    // ── Error perangkat ─────────────────────────────────────────────

    public static TheoryData<Exception> DeviceErrors => new()
    {
        // Yang dilempar NAudio 3.1 sendiri (CoreAudioException.ThrowIfFailed): DAC dicabut di tengah lagu, dipakai
        // aplikasi lain, tidak ada perangkat output (GetDefaultAudioEndpoint), dan error Core Audio lainnya
        new AudioDeviceDisconnectedException(AudioClientErrorCode.DeviceInvalidated),
        new AudioDeviceInUseException(AudioClientErrorCode.DeviceInUse),
        new AudioExclusiveModeNotAllowedException(AudioClientErrorCode.ExclusiveModeNotAllowed),
        new AudioFormatNotSupportedException(AudioClientErrorCode.UnsupportedFormat),
        new CoreAudioException(unchecked((int)0x80070490)),
        new CoreAudioException(AudioClientErrorCode.ServiceNotRunning),
        new CoreAudioException(unchecked((int)0x80070005)), // akses ke perangkat ditolak: tetap dari Core Audio

        // COMException biasa dengan HRESULT perangkat
        new COMException("Device invalidated", AudioClientErrorCode.DeviceInvalidated),
        new COMException("Device in use", AudioClientErrorCode.DeviceInUse),
        new COMException("Buffer size error", AudioClientErrorCode.BufferSizeError),
        new COMException("Element not found", unchecked((int)0x80070490)),
    };

    [Theory]
    [MemberData(nameof(DeviceErrors))]
    public void DeviceErrors_AreDeviceErrors(Exception ex) => Assert.True(AudioErrors.IsDeviceError(ex));

    // ── Exception yang dibungkus ────────────────────────────────────

    [Fact]
    public void WrappedErrors_AreJudgedByTheInnermostException()
    {
        var disconnected = new AudioDeviceDisconnectedException(AudioClientErrorCode.DeviceInvalidated);
        var missingFile = new MediaFoundationException(unchecked((int)0x80070002));

        Assert.True(AudioErrors.IsDeviceError(new InvalidOperationException("Couldn't play", disconnected)));
        Assert.True(AudioErrors.IsDeviceError(new TargetInvocationException(disconnected)));
        Assert.True(AudioErrors.IsDeviceError(new AggregateException(disconnected)));
        Assert.False(AudioErrors.IsDeviceError(new AggregateException(missingFile)));
        Assert.False(AudioErrors.IsDeviceError(new InvalidOperationException("Couldn't play",
            new COMException("Device-like HRESULT from the decoder", unchecked((int)0x80070002)))));
    }

    private static Exception? OpenReader(string path) => Record.Exception(() =>
    {
        using var reader = new MediaFoundationReader(path);
    });

    private static void AssertFileError(Exception? ex, uint expectedHResult)
    {
        var error = Assert.IsType<MediaFoundationException>(ex);
        Assert.Equal(unchecked((int)expectedHResult), error.HResult);
        Assert.False(AudioErrors.IsDeviceError(error));
    }
}

namespace MusicPlayer.Library.Tests;

public sealed class ImageInfoTests
{
    [Fact]
    public void Format_IsReadFromSignature()
    {
        Assert.Equal("PNG", ImageInfo.Format([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("JPEG", ImageInfo.Format([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("", ImageInfo.Format("GIF89a"u8.ToArray()));
        Assert.Equal("", ImageInfo.Format([]));
    }
}

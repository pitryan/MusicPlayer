using System.Buffers.Binary;
using MusicPlayer.Library;
using TagLib;
using File = System.IO.File;

namespace MusicPlayer.Library.Tests;

public sealed class FlacArtworkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "MusicPlayer.FlacArtworkTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK1sAAAAASUVORK5CYII=");
    private static TagPicture Front => new(PictureType.FrontCover, "image/png", Png);

    public FlacArtworkTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);

    private string Fixture()
    {
        string path = Path.Combine(_folder, "test.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Tags", "silence.flac"), path);
        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Keep title", ["lyrics"] = "Keep lyrics" },
            [new(PictureType.BackCover, "image/png", Png, "Keep back cover")]);
        return path;
    }

    [Theory]
    [InlineData(PictureType.FrontCover, false, false)]
    [InlineData(PictureType.Other, false, false)]
    [InlineData(PictureType.FrontCover, true, false)]
    [InlineData(PictureType.FrontCover, false, true)]
    [InlineData(PictureType.Other, false, true)]
    [InlineData(PictureType.FrontCover, true, true)]
    public void ReplaceOverflowedCover_PreservesAudioTagsAndOtherPictures(PictureType type, bool last, bool png)
    {
        string path = Fixture();
        var original = File.ReadAllBytes(path);
        var audio = original[AudioOffset(original)..];
        AddOverflow(path, type, last, png);
        Assert.Contains("Change Artwork", Assert.Throws<InvalidDataException>(() => TagEditor.Read(path)).Message);

        TagEditor.Write(path, new Dictionary<string, string>(), [Front], frontCoverOnly: true);

        var tags = TagEditor.Read(path);
        Assert.Equal("Keep title", tags.Get("title"));
        Assert.Equal("Keep lyrics", tags.Get("lyrics"));
        Assert.Equal(Png, Assert.Single(tags.Pictures, p => p.Type == PictureType.FrontCover).Data);
        var back = Assert.Single(tags.Pictures, p => p.Type == PictureType.BackCover);
        Assert.Equal("Keep back cover", back.Description);
        Assert.Equal(Png, back.Data);
        var saved = File.ReadAllBytes(path);
        Assert.Equal(audio, saved[AudioOffset(saved)..]);
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("wrong-length")]
    [InlineData("missing-jpeg-end")]
    [InlineData("bad-audio-boundary")]
    [InlineData("truncated")]
    public void UncertainRecovery_LeavesOriginalUntouched(string damage)
    {
        string path = Fixture();
        var (offset, end) = AddOverflow(path, PictureType.FrontCover, last: true);
        var bytes = File.ReadAllBytes(path);
        switch (damage)
        {
            case "wrong-length": bytes[offset + 3] ^= 1; break;
            case "missing-jpeg-end": bytes[end - 1] = 0; break;
            case "bad-audio-boundary": bytes[end] = 0; break;
            case "truncated": bytes = bytes[..(end - 1)]; break;
        }
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => TagEditor.Write(path, new Dictionary<string, string>(), [Front], true));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PngWithInvalidBoundary_IsNotRemoved(bool damageEnd)
    {
        string path = Fixture();
        var (offset, end) = AddOverflow(path, PictureType.FrontCover, true, png: true);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[damageEnd ? end - 1 : offset + 4 + 41] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => TagEditor.Write(path, new Dictionary<string, string>(), [Front], true));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Fact]
    public void FrontOnly_DoesNotDiscardOversizedBackCover()
    {
        string path = Fixture();
        AddOverflow(path, PictureType.BackCover, true);
        var original = File.ReadAllBytes(path);
        Assert.Contains("preserve", Assert.Throws<InvalidDataException>(() =>
            TagEditor.Write(path, new Dictionary<string, string>(), [Front], true)).Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void FrontOnly_PreservesOtherWhenAnExplicitFrontExists()
    {
        string path = Fixture();
        TagEditor.Write(path, new Dictionary<string, string>(), [Front], true);
        AddOverflow(path, PictureType.Other, true);
        var original = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => TagEditor.Write(path, new Dictionary<string, string>(), [Front], true));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void TagOnlyEdit_DoesNotDiscardOverflowedCover()
    {
        string path = Fixture();
        AddOverflow(path, PictureType.FrontCover, true);
        var original = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "New" }));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReplaceAll_CanRemoveOverflowedBackCover()
    {
        string path = Fixture();
        AddOverflow(path, PictureType.BackCover, true);
        TagEditor.Write(path, new Dictionary<string, string>(), [Front]);
        Assert.Equal(PictureType.FrontCover, Assert.Single(TagEditor.Read(path).Pictures).Type);
    }

    [Fact]
    public void FailureAfterRecovery_LeavesOriginalAndCleansTemporary()
    {
        string path = Fixture();
        AddOverflow(path, PictureType.FrontCover, true);
        var original = File.ReadAllBytes(path);
        Assert.ThrowsAny<Exception>(() => TagEditor.Write(path,
            new Dictionary<string, string> { ["unsupported_field"] = "fail after recovery" }, [Front], true));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Fact]
    public void Recovery_HandlesLeadingId3Tag()
    {
        string path = Fixture();
        AddOverflow(path, PictureType.FrontCover, true);
        byte[] flac = File.ReadAllBytes(path);
        File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0 }.Concat(flac).ToArray());
        TagEditor.Write(path, new Dictionary<string, string>(), [Front], true);
        Assert.Equal("Keep title", TagEditor.Read(path).Get("title"));
        Assert.Contains(TagEditor.Read(path).Pictures, p => p.Type == PictureType.FrontCover && p.Data.SequenceEqual(Png));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewArtworkLimit_IncludesDescriptionAndPictureHeader(bool largeDescription)
    {
        string path = Fixture();
        var original = File.ReadAllBytes(path);
        var picture = largeDescription
            ? Front with { Description = new string('界', 0xFFFFFF / 3) }
            : Front with { Data = new byte[0xFFFFFF - 32] };
        Assert.Contains("smaller image", Assert.Throws<InvalidDataException>(() =>
            TagEditor.Write(path, new Dictionary<string, string>(), [picture], true)).Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaceOrRemoveCover_ReclaimsPaddingAndPreservesOtherData(bool remove)
    {
        string path = Fixture();
        byte[] large = new byte[6 * 1024 * 1024];
        Png.CopyTo(large, 0);
        TagEditor.Write(path, new Dictionary<string, string>(), [Front with { Data = large }], true);
        // Blok APPLICATION tidak dikenal dan SEEKTABLE harus tetap utuh ketika offset audio berubah.
        byte[] application = "TESTopaque application data"u8.ToArray();
        byte[] seekTable = new byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(seekTable.AsSpan(16), 1600);
        AppendMetadata(path, 2, application);
        AppendMetadata(path, 3, seekTable);
        AppendMetadata(path, 1, new byte[96 * 1024]);
        byte[] before = File.ReadAllBytes(path);
        byte[] audio = before[AudioOffset(before)..];

        TagEditor.Write(path, new Dictionary<string, string>(), remove ? [] : [Front], true);

        byte[] after = File.ReadAllBytes(path);
        Assert.True(before.Length - after.Length > 6 * 1024 * 1024);
        Assert.Equal(audio, after[AudioOffset(after)..]);
        Assert.DoesNotContain(Blocks(after), b => b.Type == 1);
        Assert.Contains(Blocks(after), b => b.Type == 2 && b.Data.SequenceEqual(application));
        Assert.Contains(Blocks(after), b => b.Type == 3 && b.Data.SequenceEqual(seekTable));
        var tags = TagEditor.Read(path);
        Assert.Equal("Keep title", tags.Get("title"));
        Assert.Equal("Keep lyrics", tags.Get("lyrics"));
        Assert.Equal(Png, Assert.Single(tags.Pictures, p => p.Type == PictureType.BackCover).Data);
        Assert.Equal(remove ? 0 : 1, tags.Pictures.Count(p => p.Type == PictureType.FrontCover));
        if (!remove) Assert.Equal(Png, Assert.Single(tags.Pictures, p => p.Type == PictureType.FrontCover).Data);
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [Fact]
    public void TagOnlyEdit_DoesNotCompactPadding()
    {
        string path = Fixture();
        AppendMetadata(path, 1, new byte[96 * 1024]);
        byte[] before = File.ReadAllBytes(path);
        TagEditor.Write(path, new Dictionary<string, string> { ["title"] = "Same size!" });
        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(before.Length, after.Length);
        Assert.Contains(Blocks(after), b => b.Type == 1 && b.Data.Length > 90 * 1024);
        Assert.Equal(before[AudioOffset(before)..], after[AudioOffset(after)..]);
    }

    private static void AppendMetadata(string path, byte type, byte[] payload)
    {
        byte[] old = File.ReadAllBytes(path);
        int audio = AudioOffset(old), last = 4;
        while (last + 4 + BlockLength(old, last) < audio) last += 4 + BlockLength(old, last);
        old[last] &= 0x7F;
        using var output = File.Create(path);
        output.Write(old.AsSpan(0, audio));
        output.Write(new byte[] { (byte)(type | 128), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length });
        output.Write(payload);
        output.Write(old.AsSpan(audio));
    }

    private static IEnumerable<(int Type, byte[] Data)> Blocks(byte[] bytes)
    {
        int at = 4;
        while (true)
        {
            int size = BlockLength(bytes, at);
            yield return (bytes[at] & 127, bytes.AsSpan(at + 4, size).ToArray());
            if ((bytes[at] & 128) != 0) yield break;
            at += 4 + size;
        }
    }

    // Buat pola overflow tanpa menyertakan cover atau lagu pengguna dalam fixture. Gambar sintetis ini hanya
    // menguji batas kontainer; decoding gambar tidak dipakai dalam penulisan metadata.
    private static (int Offset, int End) AddOverflow(string path, PictureType type, bool last, bool png = false)
    {
        var old = File.ReadAllBytes(path);
        int audio = AudioOffset(old);
        int previous = 4;
        while (previous + 4 + BlockLength(old, previous) < audio)
            previous += 4 + BlockLength(old, previous);
        old[previous] &= 0x7F;
        const int imageLength = 0x1000000 + 100;
        int overhead = png ? 41 : 42;
        byte[] picture = new byte[overhead + imageLength];
        BinaryPrimitives.WriteUInt32BigEndian(picture, (uint)type);
        BinaryPrimitives.WriteUInt32BigEndian(picture.AsSpan(4), png ? 9u : 10u);
        (png ? "image/png"u8 : "image/jpeg"u8).CopyTo(picture.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(picture.AsSpan(overhead - 4), imageLength);
        if (png)
        {
            Png.AsSpan(0, 8).CopyTo(picture.AsSpan(overhead));
            Png.AsSpan(Png.Length - 12).CopyTo(picture.AsSpan(picture.Length - 12));
        }
        else
        {
            picture[42] = 0xFF; picture[43] = 0xD8; picture[44] = 0xFF;
            picture[^2] = 0xFF; picture[^1] = 0xD9;
        }
        using var output = File.Create(path);
        output.Write(old.AsSpan(0, audio));
        output.Write(new byte[] { (byte)(last ? 0x86 : 6), (byte)(picture.Length >> 16), (byte)(picture.Length >> 8), (byte)picture.Length });
        output.Write(picture);
        if (!last) output.Write(new byte[] { 0x81, 0, 0, 4, 0, 0, 0, 0 });
        output.Write(old.AsSpan(audio));
        return (audio, audio + 4 + picture.Length);
    }

    private static int BlockLength(byte[] data, int at) => (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
    private static int AudioOffset(byte[] data)
    {
        int at = 4;
        while (true)
        {
            int next = at + 4 + BlockLength(data, at);
            if ((data[at] & 128) != 0) return next;
            at = next;
        }
    }
}

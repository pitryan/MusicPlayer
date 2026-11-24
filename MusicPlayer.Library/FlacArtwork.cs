using System.Buffers.Binary;
using System.Text;
using TagLib;

namespace MusicPlayer.Library;

/// <summary>
/// Menjaga batas 24-bit blok FLAC. Beberapa tagger menulis gambar lebih besar dari batas itu dan memotong
/// ukuran pada header. Pemulihan hanya membuang gambar yang memang diminta untuk diganti, pada salinan sementara.
/// </summary>
internal static class FlacArtwork
{
    private const int MaxBlockSize = 0xFFFFFF;
    private sealed record Block(long Offset, long Length, int Type, PictureType PictureType, bool Overflow);

    public static void ValidatePictures(IReadOnlyList<TagPicture>? pictures)
    {
        if (pictures is null) return;
        foreach (var picture in pictures)
        {
            long size = 32L + Encoding.Latin1.GetByteCount(picture.MimeType)
                + Encoding.UTF8.GetByteCount(picture.Description) + picture.Data.LongLength;
            if (size > MaxBlockSize)
                throw new InvalidDataException("This artwork is too large for FLAC (the picture and its description must fit in 16 MiB). Choose a smaller image.");
        }
    }

    public static void CheckReadable(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        if (Inspect(stream).Any(b => b.Overflow))
            throw new InvalidDataException("The embedded FLAC artwork exceeds the 16 MiB limit and has an invalid block length. Use Change Artwork to replace it with a smaller image.");
    }

    public static void PrepareForWrite(string temporary, IReadOnlyList<TagPicture>? pictures, bool frontCoverOnly)
    {
        using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var blocks = Inspect(stream);
        if (!blocks.Any(b => b.Overflow)) return;

        bool hasFront = blocks.Any(b => b.Type == 6 && b.PictureType == PictureType.FrontCover);
        bool Replaced(Block b) => pictures is not null && (!frontCoverOnly
            || b.PictureType == PictureType.FrontCover || (!hasFront && b.PictureType == PictureType.Other));
        if (blocks.Any(b => b.Overflow && !Replaced(b)))
            throw new InvalidDataException("The FLAC contains oversized artwork that this edit would preserve. It cannot be safely kept; the original file has not been changed.");

        // Inspect sudah memeriksa seluruh rantai metadata, batas gambar, dan awal audio sebelum satu byte ditulis.
        var kept = blocks.Where(b => !b.Overflow).ToList();
        RetainBlocks(stream, blocks, kept);
    }

    /// <summary>
    /// TagLib menyisakan ruang cover lama sebagai padding. Setelah artwork ditulis, buang ruang itu dari salinan
    /// sementara agar file mengecil dan decoder tidak perlu melewati metadata kosong berukuran besar.
    /// </summary>
    public static void RemovePadding(string temporary)
    {
        using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var blocks = Inspect(stream);
        if (blocks.Any(b => b.Overflow)) throw Invalid();
        var kept = blocks.Where(b => b.Type != 1).ToList();
        if (kept.Count != blocks.Count)
            RetainBlocks(stream, blocks, kept);
    }

    private static void RetainBlocks(Stream stream, IReadOnlyList<Block> blocks, IReadOnlyList<Block> kept)
    {
        long audioStart = blocks[^1].Offset + 4 + blocks[^1].Length;
        byte[] buffer = new byte[64 * 1024];
        long destination = blocks[0].Offset;
        for (int i = 0; i < kept.Count; i++)
        {
            var block = kept[i];
            CopyWithin(stream, block.Offset, destination, block.Length + 4, buffer);
            stream.Position = destination;
            stream.WriteByte((byte)(block.Type | (i == kept.Count - 1 ? 0x80 : 0)));
            destination += block.Length + 4;
        }
        long audioLength = stream.Length - audioStart;
        CopyWithin(stream, audioStart, destination, audioLength, buffer);
        stream.SetLength(destination + audioLength);
    }

    private static void CopyWithin(Stream stream, long source, long destination, long length, byte[] buffer)
    {
        if (source == destination) return;
        while (length > 0)
        {
            int count = (int)Math.Min(length, buffer.Length);
            stream.Position = source;
            stream.ReadExactly(buffer.AsSpan(0, count));
            stream.Position = destination;
            stream.Write(buffer.AsSpan(0, count));
            source += count;
            destination += count;
            length -= count;
        }
    }

    private static InvalidDataException Invalid() => new(
        "The FLAC artwork or metadata is damaged and cannot be safely replaced. The original file has not been changed.");

    private static List<Block> Inspect(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) != 4) return [];
            // FLAC dengan awalan ID3 tetap didukung; awalan dipertahankan saat pemulihan.
            if (header[..3].SequenceEqual("ID3"u8))
            {
                Span<byte> rest = stackalloc byte[6];
                stream.ReadExactly(rest);
                if ((rest[2] | rest[3] | rest[4] | rest[5]) >= 128) throw Invalid();
                long id3Size = (rest[2] << 21) | (rest[3] << 14) | (rest[4] << 7) | rest[5];
                stream.Position = 10 + id3Size;
                stream.ReadExactly(header);
            }
            if (!header.SequenceEqual("fLaC"u8)) return []; // jenis lain ditangani TagLib

            var blocks = new List<Block>();
            bool last;
            do
            {
                long offset = stream.Position;
                stream.ReadExactly(header);
                last = (header[0] & 128) != 0;
                int type = header[0] & 127;
                long declared = (header[1] << 16) | (header[2] << 8) | header[3];
                if (type == 127 || (blocks.Count == 0 ? type != 0 || declared != 34 : type == 0)
                    || blocks.Count >= 65536 || declared > stream.Length - stream.Position)
                    throw Invalid();

                long length = declared;
                PictureType pictureType = PictureType.Other;
                bool overflow = false;
                if (type == 6)
                {
                    long start = stream.Position;
                    long end = start + declared;
                    pictureType = (PictureType)ReadUInt(stream, end);
                    uint mimeLength = ReadUInt(stream, end);
                    if (mimeLength > end - stream.Position) throw Invalid();
                    bool jpeg = false, png = false;
                    if (mimeLength is 9 or 10)
                    {
                        byte[] mime = new byte[mimeLength];
                        stream.ReadExactly(mime);
                        jpeg = mime.AsSpan().SequenceEqual("image/jpeg"u8);
                        png = mime.AsSpan().SequenceEqual("image/png"u8);
                    }
                    else stream.Position += mimeLength;
                    uint descriptionLength = ReadUInt(stream, end);
                    if (descriptionLength > end - stream.Position) throw Invalid();
                    stream.Position += descriptionLength;
                    for (int i = 0; i < 4; i++) ReadUInt(stream, end); // lebar, tinggi, depth, palette
                    uint dataLength = ReadUInt(stream, end);
                    long dataStart = stream.Position;
                    length = dataStart - start + dataLength;
                    if (length != declared)
                    {
                        // Tidak mencari sync secara heuristik. Hanya overflow 24-bit dengan ukuran internal yang
                        // tepat, penanda awal/akhir gambar, dan rantai metadata/audio yang dapat ditelusuri.
                        if (length <= MaxBlockSize || (length & MaxBlockSize) != declared
                            || length > stream.Length - start
                            || !HasImageBoundaries(stream, dataStart, dataLength, jpeg, png))
                            throw Invalid();
                        overflow = true;
                    }
                }
                blocks.Add(new(offset, length, type, pictureType, overflow));
                stream.Position = offset + 4 + length;
            } while (!last);

            // FLAC sync dan bit reserved: mencegah batas gambar yang salah memotong awal audio.
            if (stream.ReadByte() != 0xFF || (stream.ReadByte() & 0xFE) != 0xF8) throw Invalid();
            return blocks;
        }
        catch (EndOfStreamException) { throw Invalid(); }
    }

    private static bool HasImageBoundaries(Stream stream, long start, long length, bool jpeg, bool png)
    {
        Span<byte> bytes = stackalloc byte[12];
        stream.Position = start;
        if (jpeg && length >= 5)
        {
            stream.ReadExactly(bytes[..3]);
            if (bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF) return false;
            stream.Position = start + length - 2;
            return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD9;
        }
        if (png && length >= 20)
        {
            stream.ReadExactly(bytes[..8]);
            if (!bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
            stream.Position = start + length - 12;
            stream.ReadExactly(bytes);
            // Chunk IEND kosong beserta CRC bakunya; bukan sekadar mencari huruf IEND di isi gambar.
            return bytes.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 });
        }
        return false;
    }

    private static uint ReadUInt(Stream stream, long end)
    {
        if (end - stream.Position < 4) throw Invalid();
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}

using System.Buffers.Binary;

namespace MusicPlayer.Library;

/// <summary>
/// Mengubah byte PCM little-endian (seperti keluaran decoder Windows) menjadi sampel. Sampel integer tetap dalam skala
/// aslinya (24-bit: -8388608..8388607), supaya bit yang tidak terpakai bisa dihitung.
/// </summary>
public static class PcmDecode
{
    public static int[] ToInts(ReadOnlySpan<byte> bytes, int bitsPerSample)
    {
        int size = bitsPerSample / 8;
        var samples = new int[bytes.Length / size];
        for (int i = 0; i < samples.Length; i++)
        {
            var s = bytes.Slice(i * size, size);
            samples[i] = bitsPerSample switch
            {
                8 => s[0] - 128, // PCM 8-bit tidak bertanda
                16 => BinaryPrimitives.ReadInt16LittleEndian(s),
                // 24-bit: tiga byte, lalu tanda bit ke-23 diperluas lewat geser kiri-kanan
                24 => (s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8,
                32 => BinaryPrimitives.ReadInt32LittleEndian(s),
                _ => throw new NotSupportedException($"{bitsPerSample}-bit PCM isn't supported."),
            };
        }
        return samples;
    }

    public static float[] ToFloats(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length / 4];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
        return samples;
    }
}

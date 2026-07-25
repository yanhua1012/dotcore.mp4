using System;

namespace DotCore.Mp4;

internal readonly struct BufferSegment
{
    public BufferSegment(byte[] buffer, int count)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
    }

    public byte[] Buffer { get; }
    public int Count { get; }
}

internal static class BigEndianPatch
{
    public static void PatchInt32(byte[] buffer, int validLength, long position, long value)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (validLength < 0 || validLength > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(validLength));
        }

        if (position < 0 || position > validLength - 4L)
        {
            throw new Mp4FormatException("A fragment data-offset patch position is outside the moof buffer.");
        }

        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new Mp4FormatException("A fragment data offset exceeds the signed trun range.");
        }

        var offset = checked((int)position);
        var encoded = unchecked((uint)(int)value);
        buffer[offset] = (byte)(encoded >> 24);
        buffer[offset + 1] = (byte)(encoded >> 16);
        buffer[offset + 2] = (byte)(encoded >> 8);
        buffer[offset + 3] = (byte)encoded;
    }
}

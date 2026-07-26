using System;
using System.Collections.Generic;

namespace DotCore.Mp4;

internal static class NalUnits
{
    public static IList<NalUnitRange> Normalize(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        int firstLength;
        if (!TryFindStartCode(data, 0, out firstLength))
        {
            return new List<NalUnitRange> { new NalUnitRange(data, 0, data.Length) };
        }

        var result = new List<NalUnitRange>();
        var start = firstLength;
        while (start <= data.Length)
        {
            int nextLength;
            var next = FindStartCode(data, start, out nextLength);
            var end = next < 0 ? data.Length : next;
            if (end <= start)
            {
                throw new Mp4FormatException("An Annex-B video sample contains an empty NAL unit.");
            }

            result.Add(new NalUnitRange(data, start, end - start));
            if (next < 0) break;
            start = next + nextLength;
        }

        if (result.Count == 0)
        {
            throw new Mp4FormatException("An Annex-B video sample did not contain a NAL unit.");
        }

        return result;
    }

    private static int FindStartCode(byte[] data, int from, out int codeLength)
    {
        for (var i = Math.Max(0, from); i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }

            if (data[i + 2] == 1)
            {
                codeLength = 3;
                return i;
            }

            if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            {
                codeLength = 4;
                return i;
            }
        }

        codeLength = 0;
        return -1;
    }

    private static bool TryFindStartCode(byte[] data, int from, out int codeLength)
    {
        return FindStartCode(data, from, out codeLength) == from;
    }

}

internal readonly struct NalUnitRange
{
    public NalUnitRange(byte[] backingArray, int offset, int count)
    {
        BackingArray = backingArray ?? throw new ArgumentNullException(nameof(backingArray));
        if (offset < 0 || offset > backingArray.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count < 0 || count > backingArray.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Offset = offset;
        Count = count;
    }

    public byte[] BackingArray { get; }
    public int Offset { get; }
    public int Count { get; }
    public int Length => Count;

    public byte[] ToArray()
    {
        var result = new byte[Count];
        Buffer.BlockCopy(BackingArray, Offset, result, 0, Count);
        return result;
    }
}

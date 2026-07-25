using System;
using System.Collections.Generic;

namespace DotCore.Mp4;

internal static class NalUnits
{
    public static IList<byte[]> Normalize(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        int firstLength;
        if (!TryFindStartCode(data, 0, out firstLength))
        {
            return new List<byte[]> { Copy(data) };
        }

        var result = new List<byte[]>();
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

            var nal = new byte[end - start];
            Buffer.BlockCopy(data, start, nal, 0, nal.Length);
            result.Add(nal);
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

    private static byte[] Copy(byte[] data)
    {
        var copy = new byte[data.Length];
        Buffer.BlockCopy(data, 0, copy, 0, data.Length);
        return copy;
    }
}

using System;
using System.Collections.Generic;

namespace DotCore.Mp4;

internal sealed class FragmentPayloadSource
{
    private FragmentPayloadSource(
        byte[]? contiguousData,
        IList<NalUnitRange>? ranges,
        int nalLengthSize,
        int encodedSize)
    {
        ContiguousData = contiguousData;
        Ranges = ranges;
        NalLengthSize = nalLengthSize;
        EncodedSize = encodedSize;
    }

    public byte[]? ContiguousData { get; }
    public IList<NalUnitRange>? Ranges { get; }
    public int NalLengthSize { get; }
    public int EncodedSize { get; }

    public static FragmentPayloadSource FromAudio(byte[] ownedData)
    {
        if (ownedData == null) throw new ArgumentNullException(nameof(ownedData));
        return new FragmentPayloadSource(ownedData, null, 0, ownedData.Length);
    }

    public static FragmentPayloadSource FromVideo(
        IList<NalUnitRange> ranges,
        int nalLengthSize,
        int encodedSize)
    {
        if (ranges == null) throw new ArgumentNullException(nameof(ranges));
        if (ranges.Count == 0) throw new ArgumentException("A video fragment payload requires at least one NAL range.", nameof(ranges));
        if (nalLengthSize < 1 || nalLengthSize > 4) throw new ArgumentOutOfRangeException(nameof(nalLengthSize));
        if (encodedSize <= 0) throw new ArgumentOutOfRangeException(nameof(encodedSize));
        return new FragmentPayloadSource(
            null,
            new List<NalUnitRange>(ranges),
            nalLengthSize,
            encodedSize);
    }
}

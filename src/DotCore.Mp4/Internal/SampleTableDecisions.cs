using System;
using System.Collections.Generic;

namespace DotCore.Mp4;

internal static class SampleTableDecisions
{
    public static bool RequiresCo64(IEnumerable<long> chunkOffsets)
    {
        foreach (var offset in chunkOffsets)
        {
            if (offset > uint.MaxValue) return true;
        }

        return false;
    }

    public static long ToReaderChunkOffset(ulong value)
    {
        if (value > long.MaxValue)
        {
            throw new Mp4FormatException("A co64 chunk offset exceeds the supported reader range.");
        }

        return (long)value;
    }

    public static void ValidateSampleToChunkFirstChunks(IEnumerable<uint> firstChunks)
    {
        if (firstChunks == null) throw new ArgumentNullException(nameof(firstChunks));
        var first = true;
        uint previous = 0;
        foreach (var firstChunk in firstChunks)
        {
            previous = ValidateNextSampleToChunkFirstChunk(firstChunk, previous, first);
            first = false;
        }
    }

    public static uint ValidateNextSampleToChunkFirstChunk(uint firstChunk, uint previous, bool first)
    {
        if (first && firstChunk != 1)
        {
            throw new Mp4FormatException("The first stsc entry must start at chunk 1.");
        }

        if (!first && firstChunk <= previous)
        {
            throw new Mp4FormatException("stsc first-chunk values must be strictly increasing.");
        }

        return firstChunk;
    }
}

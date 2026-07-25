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
}

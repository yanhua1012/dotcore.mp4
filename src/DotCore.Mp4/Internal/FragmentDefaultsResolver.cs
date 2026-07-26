namespace DotCore.Mp4;

internal static class FragmentDefaultsResolver
{
    public static uint ResolveValue(
        uint? trunValue,
        uint? tfhdValue,
        uint? trexValue,
        string fieldName)
    {
        if (trunValue.HasValue) return trunValue.Value;
        if (tfhdValue.HasValue) return tfhdValue.Value;
        if (trexValue.HasValue) return trexValue.Value;
        throw new Mp4FormatException(
            "A fragment sample " + fieldName +
            " cannot be resolved from trun, tfhd, or trex.");
    }

    public static uint ResolveFlags(
        uint? sampleFlags,
        uint? firstSampleFlags,
        uint sampleIndex,
        uint? tfhdFlags,
        uint? trexFlags)
    {
        if (sampleFlags.HasValue) return sampleFlags.Value;
        if (sampleIndex == 0 && firstSampleFlags.HasValue) return firstSampleFlags.Value;
        if (tfhdFlags.HasValue) return tfhdFlags.Value;
        if (trexFlags.HasValue) return trexFlags.Value;
        throw new Mp4FormatException(
            "Fragment sample flags cannot be resolved from trun, tfhd, or trex.");
    }
}

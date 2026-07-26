using DotCore.Mp4;
using Xunit;

/// <summary>
/// Movie Fragment 預設值解析器測試套件。
/// </summary>
public sealed class FragmentDefaultsResolverTests
{
    [Fact]
    public void SampleValueUsesTrunThenTfhdThenTrexPrecedence()
    {
        Assert.Equal(1U, FragmentDefaultsResolver.ResolveValue(1, 2, 3, "duration"));
        Assert.Equal(2U, FragmentDefaultsResolver.ResolveValue(null, 2, 3, "duration"));
        Assert.Equal(3U, FragmentDefaultsResolver.ResolveValue(null, null, 3, "duration"));
        Assert.Throws<Mp4FormatException>(() =>
            FragmentDefaultsResolver.ResolveValue(null, null, null, "duration"));
    }

    [Fact]
    public void FirstSampleFlagsApplyOnlyToFirstSampleBeforeTrackDefaults()
    {
        Assert.Equal(1U, FragmentDefaultsResolver.ResolveFlags(1, 2, 0, 3, 4));
        Assert.Equal(2U, FragmentDefaultsResolver.ResolveFlags(null, 2, 0, 3, 4));
        Assert.Equal(3U, FragmentDefaultsResolver.ResolveFlags(null, 2, 1, 3, 4));
        Assert.Equal(4U, FragmentDefaultsResolver.ResolveFlags(null, null, 1, null, 4));
        Assert.Throws<Mp4FormatException>(() =>
            FragmentDefaultsResolver.ResolveFlags(null, null, 0, null, null));
    }
}

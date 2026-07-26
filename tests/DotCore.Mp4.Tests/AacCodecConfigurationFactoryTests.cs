using System;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

/// <summary>
/// AAC 編解碼器組態工廠方法測試套件。
/// </summary>
public sealed class AacCodecConfigurationFactoryTests
{
    [Fact]
    public void CreateAacLc_WithStandard44100HzStereo_ProducesCorrectConfig()
    {
        var config = AacCodecConfiguration.CreateAacLc(44100, 2);

        Assert.Equal(44100, config.SampleRate);
        Assert.Equal(44100, config.SampleRateHz);
        Assert.Equal(2, config.ChannelConfiguration);
        Assert.Equal(2, config.Channels);
        Assert.Equal(2, config.AudioObjectType);
        Assert.Equal(new byte[] { 0x12, 0x10 }, config.AudioSpecificConfig);
    }

    [Fact]
    public void Constructor_WithSampleRateAndChannels_ProducesCorrectConfig()
    {
        var config = new AacCodecConfiguration(44100, 2);

        Assert.Equal(44100, config.SampleRate);
        Assert.Equal(2, config.ChannelConfiguration);
        Assert.Equal(2, config.AudioObjectType);
        Assert.Equal(new byte[] { 0x12, 0x10 }, config.AudioSpecificConfig);
    }

    [Fact]
    public void Create_WithNonStandardSampleRate_EncodesIndex15()
    {
        var config = AacCodecConfiguration.Create(50000, 2);

        Assert.Equal(50000, config.SampleRate);
        Assert.Equal(2, config.ChannelConfiguration);
        Assert.Equal(2, config.AudioObjectType);
        Assert.Equal(5, config.AudioSpecificConfig.Length);

        var roundtripped = AacCodecConfiguration.FromAudioSpecificConfig(config.AudioSpecificConfig);
        Assert.Equal(50000, roundtripped.SampleRate);
        Assert.Equal(2, roundtripped.ChannelConfiguration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    public void Create_WithInvalidChannelConfiguration_ThrowsArgumentOutOfRangeException(int invalidChannelConfig)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AacCodecConfiguration.CreateAacLc(44100, invalidChannelConfig));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacCodecConfiguration(44100, invalidChannelConfig));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-44100)]
    public void Create_WithInvalidSampleRate_ThrowsArgumentOutOfRangeException(int invalidSampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AacCodecConfiguration.CreateAacLc(invalidSampleRate, 2));
    }
}

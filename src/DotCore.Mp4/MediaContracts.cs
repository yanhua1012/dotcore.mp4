using System;

namespace DotCore.Mp4;

/// <summary>Video codecs supported by the MP4 component.</summary>
public enum VideoCodec
{
    H264 = 1,
    H265 = 2
}

/// <summary>Immutable codec configuration used by the video writer and reader.</summary>
public sealed class VideoCodecConfiguration
{
    private readonly byte[] _vps;
    private readonly byte[] _sps;
    private readonly byte[] _pps;

    /// <summary>Creates an H.264 configuration containing SPS and PPS.</summary>
    public VideoCodecConfiguration(
        VideoCodec codec,
        byte[] sps,
        byte[] pps,
        int nalLengthSize = 4,
        int width = 0,
        int height = 0)
    {
        if (codec != VideoCodec.H264)
        {
            throw new ArgumentException("The two-parameter-set constructor is only valid for H.264.", nameof(codec));
        }

        ValidateParameterSets(sps, nameof(sps), pps, nameof(pps), null, nalLengthSize, width, height);
        ValidateParameterSetNalType(sps, nameof(sps), VideoCodec.H264, 7, "SPS");
        ValidateParameterSetNalType(pps, nameof(pps), VideoCodec.H264, 8, "PPS");
        Codec = codec;
        _vps = Array.Empty<byte>();
        _sps = Copy(sps);
        _pps = Copy(pps);
        NalLengthSize = nalLengthSize;
        Width = width;
        Height = height;
    }

    /// <summary>Creates an H.265 configuration containing VPS, SPS, and PPS.</summary>
    public VideoCodecConfiguration(
        VideoCodec codec,
        byte[] vps,
        byte[] sps,
        byte[] pps,
        int nalLengthSize = 4,
        int width = 0,
        int height = 0)
    {
        if (codec != VideoCodec.H265)
        {
            throw new ArgumentException("The three-parameter-set constructor is only valid for H.265.", nameof(codec));
        }

        if (vps == null)
        {
            throw new ArgumentNullException(nameof(vps));
        }

        ValidateParameterSets(sps, nameof(sps), pps, nameof(pps), vps, nalLengthSize, width, height);
        ValidateParameterSetNalType(vps, nameof(vps), VideoCodec.H265, 32, "VPS");
        ValidateParameterSetNalType(sps, nameof(sps), VideoCodec.H265, 33, "SPS");
        ValidateParameterSetNalType(pps, nameof(pps), VideoCodec.H265, 34, "PPS");
        Codec = codec;
        _vps = Copy(vps);
        _sps = Copy(sps);
        _pps = Copy(pps);
        NalLengthSize = nalLengthSize;
        Width = width;
        Height = height;
    }

    /// <summary>Creates an H.264 configuration without relying on constructor overload selection.</summary>
    public static VideoCodecConfiguration CreateH264(
        byte[] sps,
        byte[] pps,
        int nalLengthSize = 4,
        int width = 0,
        int height = 0)
    {
        return new VideoCodecConfiguration(VideoCodec.H264, sps, pps, nalLengthSize, width, height);
    }

    /// <summary>Creates an H.265 configuration without relying on constructor overload selection.</summary>
    public static VideoCodecConfiguration CreateH265(
        byte[] vps,
        byte[] sps,
        byte[] pps,
        int nalLengthSize = 4,
        int width = 0,
        int height = 0)
    {
        return new VideoCodecConfiguration(VideoCodec.H265, vps, sps, pps, nalLengthSize, width, height);
    }

    public VideoCodec Codec { get; }

    /// <summary>Returns a defensive copy of the HEVC VPS, or an empty array for H.264.</summary>
    public byte[] Vps => Copy(_vps);

    /// <summary>Returns a defensive copy of the sequence parameter set.</summary>
    public byte[] Sps => Copy(_sps);

    /// <summary>Returns a defensive copy of the picture parameter set.</summary>
    public byte[] Pps => Copy(_pps);

    public int NalLengthSize { get; }

    /// <summary>Alias for callers that use the ISO BMFF terminology.</summary>
    public int NalUnitLengthSize => NalLengthSize;

    /// <summary>Optional coded width written into the visual sample entry.</summary>
    public int Width { get; }

    /// <summary>Optional coded height written into the visual sample entry.</summary>
    public int Height { get; }

    internal byte[] VpsBytes => _vps;
    internal byte[] SpsBytes => _sps;
    internal byte[] PpsBytes => _pps;

    private static void ValidateParameterSets(
        byte[] sps,
        string spsName,
        byte[] pps,
        string ppsName,
        byte[]? vps,
        int nalLengthSize,
        int width,
        int height)
    {
        if (vps != null && vps.Length == 0)
        {
            throw new ArgumentException("The HEVC VPS must not be empty.", nameof(vps));
        }

        if (sps == null)
        {
            throw new ArgumentNullException(spsName);
        }

        if (sps.Length == 0)
        {
            throw new ArgumentException("The SPS must not be empty.", spsName);
        }

        if (pps == null)
        {
            throw new ArgumentNullException(ppsName);
        }

        if (pps.Length == 0)
        {
            throw new ArgumentException("The PPS must not be empty.", ppsName);
        }

        if (nalLengthSize < 1 || nalLengthSize > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(nalLengthSize), "NAL length size must be between one and four bytes.");
        }

        if (width < 0 || height < 0 || (width == 0) != (height == 0) || width > ushort.MaxValue || height > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must both be zero or positive 16-bit values.");
        }
    }

    private static void ValidateParameterSetNalType(
        byte[] value,
        string parameterName,
        VideoCodec codec,
        int expectedType,
        string displayName)
    {
        System.Collections.Generic.IList<NalUnitRange> units;
        try
        {
            units = NalUnits.Normalize(value);
        }
        catch (Mp4FormatException ex)
        {
            throw new ArgumentException("The " + displayName + " is not a valid NAL unit.", parameterName, ex);
        }

        if (units.Count != 1 || units[0].Count < (codec == VideoCodec.H265 ? 2 : 1))
        {
            throw new ArgumentException(
                "The " + displayName + " must contain exactly one complete NAL unit.",
                parameterName);
        }

        var actualType = codec == VideoCodec.H264
            ? units[0].BackingArray[units[0].Offset] & 0x1f
            : (units[0].BackingArray[units[0].Offset] >> 1) & 0x3f;
        if (actualType != expectedType)
        {
            throw new ArgumentException(
                "The " + displayName + " NAL unit has type " + actualType +
                " but " + expectedType + " is required for " + codec + ".",
                parameterName);
        }
    }

    private static byte[] Copy(byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return copy;
    }
}

/// <summary>Immutable AAC configuration represented by an MPEG-4 AudioSpecificConfig.</summary>
public sealed class AacCodecConfiguration
{
    private readonly byte[] _audioSpecificConfig;

    public AacCodecConfiguration(byte[] audioSpecificConfig, int sampleRate, int channelConfiguration)
    {
        if (audioSpecificConfig == null)
        {
            throw new ArgumentNullException(nameof(audioSpecificConfig));
        }

        if (audioSpecificConfig.Length == 0)
        {
            throw new ArgumentException("AudioSpecificConfig must not be empty.", nameof(audioSpecificConfig));
        }

        var parsed = AacConfigParser.Parse(audioSpecificConfig);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channelConfiguration < 1 || channelConfiguration > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(channelConfiguration), "Only explicit MPEG-4 channel configurations one through seven are supported.");
        }

        if (parsed.SampleRate != sampleRate)
        {
            throw new ArgumentException("The declared AAC sample rate does not match AudioSpecificConfig.", nameof(sampleRate));
        }

        if (parsed.ChannelConfiguration != channelConfiguration)
        {
            throw new ArgumentException("The declared AAC channel configuration does not match AudioSpecificConfig.", nameof(channelConfiguration));
        }

        _audioSpecificConfig = Copy(audioSpecificConfig);
        SampleRate = sampleRate;
        ChannelConfiguration = channelConfiguration;
        AudioObjectType = parsed.AudioObjectType;
    }

    public AacCodecConfiguration(int sampleRate, int channelConfiguration, byte[] audioSpecificConfig)
        : this(audioSpecificConfig, sampleRate, channelConfiguration)
    {
    }

    public static AacCodecConfiguration FromAudioSpecificConfig(byte[] audioSpecificConfig)
    {
        if (audioSpecificConfig == null)
        {
            throw new ArgumentNullException(nameof(audioSpecificConfig));
        }

        var parsed = AacConfigParser.Parse(audioSpecificConfig);
        return new AacCodecConfiguration(audioSpecificConfig, parsed.SampleRate, parsed.ChannelConfiguration);
    }

    public byte[] AudioSpecificConfig => Copy(_audioSpecificConfig);
    public int SampleRate { get; }
    public int SampleRateHz => SampleRate;
    public int ChannelConfiguration { get; }
    public int Channels => ChannelConfiguration;
    public int AudioObjectType { get; }

    internal byte[] AudioSpecificConfigBytes => _audioSpecificConfig;

    private static byte[] Copy(byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return copy;
    }
}

/// <summary>An encoded video NAL unit with presentation and decode timing.</summary>
public sealed class EncodedVideoNalUnit
{
    private readonly byte[] _data;

    public EncodedVideoNalUnit(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration,
        bool isKeyFrame)
    {
        Validate(data, presentationTimestamp, decodeTimestamp, duration);
        _data = Copy(data);
        PresentationTimestamp = presentationTimestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
        IsKeyFrame = isKeyFrame;
    }

    private EncodedVideoNalUnit(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration,
        bool isKeyFrame,
        OwnedData ownership)
    {
        _ = ownership;
        Validate(data, presentationTimestamp, decodeTimestamp, duration);
        _data = data;
        PresentationTimestamp = presentationTimestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
        IsKeyFrame = isKeyFrame;
    }

    private static void Validate(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Length == 0)
        {
            throw new ArgumentException("A NAL unit must not be empty.", nameof(data));
        }

        if (presentationTimestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestamp));
        }

        if (decodeTimestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeTimestamp));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }

    public byte[] Data => Copy(_data);
    public TimeSpan PresentationTimestamp { get; }
    public TimeSpan DecodeTimestamp { get; }
    public TimeSpan Duration { get; }
    public bool IsKeyFrame { get; }
    public TimeSpan Pts => PresentationTimestamp;
    public TimeSpan Dts => DecodeTimestamp;

    internal byte[] DataBytes => _data;

    internal static EncodedVideoNalUnit FromOwnedData(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration,
        bool isKeyFrame)
    {
        return new EncodedVideoNalUnit(
            data,
            presentationTimestamp,
            decodeTimestamp,
            duration,
            isKeyFrame,
            OwnedData.Value);
    }

    private static byte[] Copy(byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return copy;
    }

    private enum OwnedData
    {
        Value
    }
}

/// <summary>An encoded AAC access unit with presentation and decode timing.</summary>
public sealed class EncodedAudioSample
{
    private readonly byte[] _data;

    public EncodedAudioSample(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration)
    {
        Validate(data, presentationTimestamp, decodeTimestamp, duration);
        _data = Copy(data);
        PresentationTimestamp = presentationTimestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
    }

    private EncodedAudioSample(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration,
        OwnedData ownership)
    {
        _ = ownership;
        Validate(data, presentationTimestamp, decodeTimestamp, duration);
        _data = data;
        PresentationTimestamp = presentationTimestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
    }

    private static void Validate(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Length == 0)
        {
            throw new ArgumentException("An AAC access unit must not be empty.", nameof(data));
        }

        if (presentationTimestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestamp));
        }

        if (decodeTimestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeTimestamp));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }

    public byte[] Data => Copy(_data);
    public TimeSpan PresentationTimestamp { get; }
    public TimeSpan DecodeTimestamp { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Pts => PresentationTimestamp;
    public TimeSpan Dts => DecodeTimestamp;

    internal byte[] DataBytes => _data;

    internal static EncodedAudioSample FromOwnedData(
        byte[] data,
        TimeSpan presentationTimestamp,
        TimeSpan decodeTimestamp,
        TimeSpan duration)
    {
        return new EncodedAudioSample(
            data,
            presentationTimestamp,
            decodeTimestamp,
            duration,
            OwnedData.Value);
    }

    private static byte[] Copy(byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return copy;
    }

    private enum OwnedData
    {
        Value
    }
}

public sealed class VideoNalUnitReadEventArgs : EventArgs
{
    public VideoNalUnitReadEventArgs(EncodedVideoNalUnit sample, VideoCodecConfiguration configuration)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Data = sample.Data;
        PresentationTimestamp = sample.PresentationTimestamp;
        DecodeTimestamp = sample.DecodeTimestamp;
        Duration = sample.Duration;
        IsKeyFrame = sample.IsKeyFrame;
    }

    public byte[] Data { get; }
    public TimeSpan PresentationTimestamp { get; }
    public TimeSpan DecodeTimestamp { get; }
    public TimeSpan Duration { get; }
    public bool IsKeyFrame { get; }
    public VideoCodecConfiguration Configuration { get; }
    public TimeSpan Pts => PresentationTimestamp;
    public TimeSpan Dts => DecodeTimestamp;
}

public sealed class AacSampleReadEventArgs : EventArgs
{
    public AacSampleReadEventArgs(EncodedAudioSample sample, AacCodecConfiguration configuration)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Data = sample.Data;
        PresentationTimestamp = sample.PresentationTimestamp;
        DecodeTimestamp = sample.DecodeTimestamp;
        Duration = sample.Duration;
    }

    public byte[] Data { get; }
    public TimeSpan PresentationTimestamp { get; }
    public TimeSpan DecodeTimestamp { get; }
    public TimeSpan Duration { get; }
    public AacCodecConfiguration Configuration { get; }
    public int SampleRate => Configuration.SampleRate;
    public int ChannelConfiguration => Configuration.ChannelConfiguration;
    public byte[] AudioSpecificConfig => Configuration.AudioSpecificConfig;
    public TimeSpan Pts => PresentationTimestamp;
    public TimeSpan Dts => DecodeTimestamp;
}

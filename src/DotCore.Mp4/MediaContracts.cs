using System;

namespace DotCore.Mp4;

/// <summary>
/// MP4 元件所支援的視訊編解碼器 (Video Codecs)。
/// </summary>
public enum VideoCodec
{
    /// <summary>
    /// Advanced Video Coding (AVC / H.264) 編解碼器。
    /// </summary>
    H264 = 1,

    /// <summary>
    /// High Efficiency Video Coding (HEVC / H.265) 編解碼器。
    /// </summary>
    H265 = 2
}

/// <summary>
/// 表示由視訊寫入器 (Writer) 與讀取器 (Reader) 使用的不可變視訊編解碼器組態資訊。
/// </summary>
public sealed class VideoCodecConfiguration
{
    private readonly byte[] _vps;
    private readonly byte[] _sps;
    private readonly byte[] _pps;

    /// <summary>
    /// 初始化包含 SPS 與 PPS 的 H.264 <see cref="VideoCodecConfiguration"/> 類別新實例。
    /// </summary>
    /// <param name="codec">視訊編解碼器型別（必須為 <see cref="VideoCodec.H264"/>）。</param>
    /// <param name="sps">序列參數集 (Sequence Parameter Set, SPS) 位元組陣列。</param>
    /// <param name="pps">圖像參數集 (Picture Parameter Set, PPS) 位元組陣列。</param>
    /// <param name="nalLengthSize">NAL 單元長度欄位大小（位元組數，範圍 1~4，預設為 4）。</param>
    /// <param name="width">視訊寬度 (像素)，預設為 0。</param>
    /// <param name="height">視訊高度 (像素)，預設為 0。</param>
    /// <exception cref="ArgumentException">當 <paramref name="codec"/> 不為 H.264 或參數集不符合規格時擲出。</exception>
    /// <exception cref="ArgumentNullException">當 <paramref name="sps"/> 或 <paramref name="pps"/> 為 null 時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">當 <paramref name="nalLengthSize"/>、<paramref name="width"/> 或 <paramref name="height"/> 超出有效範圍時擲出。</exception>
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

    /// <summary>
    /// 初始化包含 VPS、SPS 與 PPS 的 H.265 (HEVC) <see cref="VideoCodecConfiguration"/> 類別新實例。
    /// </summary>
    /// <param name="codec">視訊編解碼器型別（必須為 <see cref="VideoCodec.H265"/>）。</param>
    /// <param name="vps">視訊參數集 (Video Parameter Set, VPS) 位元組陣列。</param>
    /// <param name="sps">序列參數集 (Sequence Parameter Set, SPS) 位元組陣列。</param>
    /// <param name="pps">圖像參數集 (Picture Parameter Set, PPS) 位元組陣列。</param>
    /// <param name="nalLengthSize">NAL 單元長度欄位大小（位元組數，範圍 1~4，預設為 4）。</param>
    /// <param name="width">視訊寬度 (像素)，預設為 0。</param>
    /// <param name="height">視訊高度 (像素)，預設為 0。</param>
    /// <exception cref="ArgumentException">當 <paramref name="codec"/> 不為 H.265 或參數集不符合規格時擲出。</exception>
    /// <exception cref="ArgumentNullException">當 <paramref name="vps"/>、<paramref name="sps"/> 或 <paramref name="pps"/> 為 null 時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">當 <paramref name="nalLengthSize"/>、<paramref name="width"/> 或 <paramref name="height"/> 超出有效範圍時擲出。</exception>
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

    /// <summary>
    /// 建立 H.264 <see cref="VideoCodecConfiguration"/> 實例的靜態工廠方法。
    /// </summary>
    /// <param name="sps">序列參數集 (SPS) 位元組陣列。</param>
    /// <param name="pps">圖像參數集 (PPS) 位元組陣列。</param>
    /// <param name="nalLengthSize">NAL 單元長度欄位大小（位元組數，範圍 1~4，預設為 4）。</param>
    /// <param name="width">視訊寬度 (像素)，預設為 0。</param>
    /// <param name="height">視訊高度 (像素)，預設為 0。</param>
    /// <returns>已初始化的 H.264 <see cref="VideoCodecConfiguration"/> 實例。</returns>
    public static VideoCodecConfiguration CreateH264(
        byte[] sps,
        byte[] pps,
        int nalLengthSize = 4,
        int width = 0,
        int height = 0)
    {
        return new VideoCodecConfiguration(VideoCodec.H264, sps, pps, nalLengthSize, width, height);
    }

    /// <summary>
    /// 建立 H.265 (HEVC) <see cref="VideoCodecConfiguration"/> 實例的靜態工廠方法。
    /// </summary>
    /// <param name="vps">視訊參數集 (VPS) 位元組陣列。</param>
    /// <param name="sps">序列參數集 (SPS) 位元組陣列。</param>
    /// <param name="pps">圖像參數集 (PPS) 位元組陣列。</param>
    /// <param name="nalLengthSize">NAL 單元長度欄位大小（位元組數，範圍 1~4，預設為 4）。</param>
    /// <param name="width">視訊寬度 (像素)，預設為 0。</param>
    /// <param name="height">視訊高度 (像素)，預設為 0。</param>
    /// <returns>已初始化的 H.265 <see cref="VideoCodecConfiguration"/> 實例。</returns>
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

    /// <summary>
    /// 取得視訊編解碼器型別 (<see cref="VideoCodec"/>)。
    /// </summary>
    public VideoCodec Codec { get; }

    /// <summary>
    /// 取得 HEVC VPS 位元組陣列的防禦性複製；若為 H.264 則回傳空陣列。
    /// </summary>
    public byte[] Vps => Copy(_vps);

    /// <summary>
    /// 取得序列參數集 (SPS) 位元組陣列的防禦性複製。
    /// </summary>
    public byte[] Sps => Copy(_sps);

    /// <summary>
    /// 取得圖像參數集 (PPS) 位元組陣列的防禦性複製。
    /// </summary>
    public byte[] Pps => Copy(_pps);

    /// <summary>
    /// 取得 NAL 單元長度欄位大小（位元組數）。
    /// </summary>
    public int NalLengthSize { get; }

    /// <summary>
    /// 取得符合 ISO BMFF 術語的 NAL 單元長度欄位大小別名。
    /// </summary>
    public int NalUnitLengthSize => NalLengthSize;

    /// <summary>
    /// 取得寫入視覺樣本條目的視訊寬度 (像素)。
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// 取得寫入視覺樣本條目的視訊高度 (像素)。
    /// </summary>
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

/// <summary>
/// 表示不可變的 AAC 音訊解碼組態（MPEG-4 AudioSpecificConfig）。
/// <para>
/// MPEG-4 AudioSpecificConfig 位元結構如下：
/// <list type="bullet">
///   <item><description><b>Audio Object Type (5 bits)</b>: 音訊物件類型（例如 2 代表 AAC-LC）。</description></item>
///   <item><description><b>Sampling Frequency Index (4 bits)</b>: 取樣率索引（0~12 代表標準取樣率；15 代表隨後跟隨 24-bit 自訂取樣率）。</description></item>
///   <item><description><b>Sampling Frequency (24 bits)</b>: 當索引為 15 時存在，表示明確的取樣率整數值。</description></item>
///   <item><description><b>Channel Configuration (4 bits)</b>: 聲道配置（1~7 代表 1 至 7 聲道數）。</description></item>
///   <item><description><b>Reserved / Extension (3 bits)</b>: 對齊至位元組邊界的保留位元（補零）。</description></item>
/// </list>
/// 標準取樣率包含 2 位元組 (16-bit) 標頭，非標準取樣率則為 5 位元組 (40-bit) 標頭。
/// </para>
/// </summary>
public sealed class AacCodecConfiguration
{
    private readonly byte[] _audioSpecificConfig;

    /// <summary>
    /// 使用指定的取樣率與聲道配置初始化 AAC-LC <see cref="AacCodecConfiguration"/> 類別的新實例。
    /// </summary>
    /// <param name="sampleRate">音訊取樣率 (Hz)。</param>
    /// <param name="channelConfiguration">聲道配置數 (1~7)。</param>
    public AacCodecConfiguration(int sampleRate, int channelConfiguration)
        : this(AacConfigParser.Encode(sampleRate, channelConfiguration, 2), sampleRate, channelConfiguration)
    {
    }

    /// <summary>
    /// 使用既有的 AudioSpecificConfig 二進位標頭、取樣率與聲道配置初始化 <see cref="AacCodecConfiguration"/> 類別的新實例。
    /// </summary>
    /// <param name="audioSpecificConfig">MPEG-4 AudioSpecificConfig 二進位標頭。</param>
    /// <param name="sampleRate">宣告的取樣率 (Hz)。</param>
    /// <param name="channelConfiguration">宣告的聲道配置數 (1~7)。</param>
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

    /// <summary>
    /// 使用取樣率、聲道配置與既有的 AudioSpecificConfig 二進位標頭初始化 <see cref="AacCodecConfiguration"/> 類別的新實例。
    /// </summary>
    /// <param name="sampleRate">宣告的取樣率 (Hz)。</param>
    /// <param name="channelConfiguration">宣告的聲道配置數 (1~7)。</param>
    /// <param name="audioSpecificConfig">MPEG-4 AudioSpecificConfig 二進位標頭。</param>
    public AacCodecConfiguration(int sampleRate, int channelConfiguration, byte[] audioSpecificConfig)
        : this(audioSpecificConfig, sampleRate, channelConfiguration)
    {
    }

    /// <summary>
    /// 依指定的取樣率、聲道配置與音訊物件類型動態建立 <see cref="AacCodecConfiguration"/> 實例，並自動編碼 AudioSpecificConfig 標頭。
    /// </summary>
    /// <param name="sampleRate">音訊取樣率 (Hz)。</param>
    /// <param name="channelConfiguration">聲道配置數 (1~7)。</param>
    /// <param name="audioObjectType">音訊物件類型（預設為 2 即 AAC-LC）。</param>
    /// <returns>對應的 <see cref="AacCodecConfiguration"/> 實例。</returns>
    public static AacCodecConfiguration Create(int sampleRate, int channelConfiguration, int audioObjectType = 2)
    {
        var asc = AacConfigParser.Encode(sampleRate, channelConfiguration, audioObjectType);
        return new AacCodecConfiguration(asc, sampleRate, channelConfiguration);
    }

    /// <summary>
    /// 依指定的取樣率與聲道配置動態建立 AAC-LC (Audio Object Type = 2) 的 <see cref="AacCodecConfiguration"/> 實例。
    /// </summary>
    /// <param name="sampleRate">音訊取樣率 (Hz)。</param>
    /// <param name="channelConfiguration">聲道配置數 (1~7)。</param>
    /// <returns>對應的 AAC-LC <see cref="AacCodecConfiguration"/> 實例。</returns>
    public static AacCodecConfiguration CreateAacLc(int sampleRate, int channelConfiguration)
    {
        return Create(sampleRate, channelConfiguration, 2);
    }

    /// <summary>
    /// 從 MPEG-4 AudioSpecificConfig 二進位標頭解析並建立 <see cref="AacCodecConfiguration"/> 實例。
    /// </summary>
    /// <param name="audioSpecificConfig">MPEG-4 AudioSpecificConfig 位元組陣列。</param>
    /// <returns>解析後的 <see cref="AacCodecConfiguration"/> 實例。</returns>
    public static AacCodecConfiguration FromAudioSpecificConfig(byte[] audioSpecificConfig)
    {
        if (audioSpecificConfig == null)
        {
            throw new ArgumentNullException(nameof(audioSpecificConfig));
        }

        var parsed = AacConfigParser.Parse(audioSpecificConfig);
        return new AacCodecConfiguration(audioSpecificConfig, parsed.SampleRate, parsed.ChannelConfiguration);
    }

    /// <summary>
    /// 取得 MPEG-4 AudioSpecificConfig 二進位標頭的防禦性複製。
    /// </summary>
    public byte[] AudioSpecificConfig => Copy(_audioSpecificConfig);

    /// <summary>
    /// 取得音訊取樣率 (Hz)。
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// 取得音訊取樣率 (Hz) 別名。
    /// </summary>
    public int SampleRateHz => SampleRate;

    /// <summary>
    /// 取得 MPEG-4 聲道配置數 (1~7)。
    /// </summary>
    public int ChannelConfiguration { get; }

    /// <summary>
    /// 取得聲道數別名。
    /// </summary>
    public int Channels => ChannelConfiguration;

    /// <summary>
    /// 取得 MPEG-4 音訊物件類型 (Audio Object Type, AOT)。
    /// </summary>
    public int AudioObjectType { get; }

    internal byte[] AudioSpecificConfigBytes => _audioSpecificConfig;

    private static byte[] Copy(byte[] value)
    {
        var copy = new byte[value.Length];
        Buffer.BlockCopy(value, 0, copy, 0, value.Length);
        return copy;
    }
}

/// <summary>
/// 表示包含顯示與解碼時間標記的已編碼視訊 NAL 單元 (Access Unit)。
/// </summary>
public sealed class EncodedVideoNalUnit
{
    private readonly byte[] _data;

    /// <summary>
    /// 初始化 <see cref="EncodedVideoNalUnit"/> 類別的新實例。
    /// </summary>
    /// <param name="data">已編碼的 NAL 單元資料位元組陣列。</param>
    /// <param name="presentationTimestamp">顯示時間標記 (PTS)。</param>
    /// <param name="decodeTimestamp">解碼時間標記 (DTS)。</param>
    /// <param name="duration">影格持續時間。</param>
    /// <param name="isKeyFrame">指示此 NAL 單元是否為關鍵影格 (Keyframe / IDR)。</param>
    /// <exception cref="ArgumentNullException">當 <paramref name="data"/> 為 null 時擲出。</exception>
    /// <exception cref="ArgumentException">當 <paramref name="data"/> 為長度零的陣列時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">當時間標記為負值或持續時間小於等於零時擲出。</exception>
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

    /// <summary>
    /// 取得已編碼 NAL 單元資料的防禦性複製。
    /// </summary>
    public byte[] Data => Copy(_data);

    /// <summary>
    /// 取得顯示時間標記 (Presentation Timestamp, PTS)。
    /// </summary>
    public TimeSpan PresentationTimestamp { get; }

    /// <summary>
    /// 取得解碼時間標記 (Decode Timestamp, DTS)。
    /// </summary>
    public TimeSpan DecodeTimestamp { get; }

    /// <summary>
    /// 取得影格持續時間。
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// 取得指示此 NAL 單元是否為關鍵影格的布林值。
    /// </summary>
    public bool IsKeyFrame { get; }

    /// <summary>
    /// 取得顯示時間標記 (PTS) 別名。
    /// </summary>
    public TimeSpan Pts => PresentationTimestamp;

    /// <summary>
    /// 取得解碼時間標記 (DTS) 別名。
    /// </summary>
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

/// <summary>
/// 表示包含顯示與解碼時間標記的已編碼 AAC 音訊樣本 (Access Unit)。
/// </summary>
public sealed class EncodedAudioSample
{
    private readonly byte[] _data;

    /// <summary>
    /// 初始化 <see cref="EncodedAudioSample"/> 類別的新實例。
    /// </summary>
    /// <param name="data">已編碼的 AAC 樣本資料位元組陣列。</param>
    /// <param name="presentationTimestamp">顯示時間標記 (PTS)。</param>
    /// <param name="decodeTimestamp">解碼時間標記 (DTS)。</param>
    /// <param name="duration">樣本持續時間。</param>
    /// <exception cref="ArgumentNullException">當 <paramref name="data"/> 為 null 時擲出。</exception>
    /// <exception cref="ArgumentException">當 <paramref name="data"/> 為長度零的陣列時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">當時間標記為負值或持續時間小於等於零時擲出。</exception>
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

    /// <summary>
    /// 取得已編碼 AAC 樣本資料的防禦性複製。
    /// </summary>
    public byte[] Data => Copy(_data);

    /// <summary>
    /// 取得顯示時間標記 (Presentation Timestamp, PTS)。
    /// </summary>
    public TimeSpan PresentationTimestamp { get; }

    /// <summary>
    /// 取得解碼時間標記 (Decode Timestamp, DTS)。
    /// </summary>
    public TimeSpan DecodeTimestamp { get; }

    /// <summary>
    /// 取得樣本持續時間。
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// 取得顯示時間標記 (PTS) 別名。
    /// </summary>
    public TimeSpan Pts => PresentationTimestamp;

    /// <summary>
    /// 取得解碼時間標記 (DTS) 別名。
    /// </summary>
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

/// <summary>
/// 為視訊 NAL 單元讀取事件提供資料。
/// </summary>
public sealed class VideoNalUnitReadEventArgs : EventArgs
{
    /// <summary>
    /// 使用指定的視訊 NAL 單元與編解碼器組態初始化 <see cref="VideoNalUnitReadEventArgs"/> 類別的新實例。
    /// </summary>
    /// <param name="sample">已編碼的視訊 NAL 單元。</param>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    /// <exception cref="ArgumentNullException">當 <paramref name="sample"/> 或 <paramref name="configuration"/> 為 null 時擲出。</exception>
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

    /// <summary>
    /// 取得視訊 NAL 單元資料的防禦性複製。
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// 取得顯示時間標記 (Presentation Timestamp, PTS)。
    /// </summary>
    public TimeSpan PresentationTimestamp { get; }

    /// <summary>
    /// 取得解碼時間標記 (Decode Timestamp, DTS)。
    /// </summary>
    public TimeSpan DecodeTimestamp { get; }

    /// <summary>
    /// 取得影格持續時間。
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// 取得指示此 NAL 單元是否為關鍵影格的布林值。
    /// </summary>
    public bool IsKeyFrame { get; }

    /// <summary>
    /// 取得對應的視訊編解碼器組態資訊。
    /// </summary>
    public VideoCodecConfiguration Configuration { get; }

    /// <summary>
    /// 取得顯示時間標記 (PTS) 別名。
    /// </summary>
    public TimeSpan Pts => PresentationTimestamp;

    /// <summary>
    /// 取得解碼時間標記 (DTS) 別名。
    /// </summary>
    public TimeSpan Dts => DecodeTimestamp;
}

/// <summary>
/// 為 AAC 音訊樣本讀取事件提供資料。
/// </summary>
public sealed class AacSampleReadEventArgs : EventArgs
{
    /// <summary>
    /// 使用指定的 AAC 音訊樣本與編解碼器組態初始化 <see cref="AacSampleReadEventArgs"/> 類別的新實例。
    /// </summary>
    /// <param name="sample">已編碼的 AAC 音訊樣本。</param>
    /// <param name="configuration">AAC 音訊編解碼器組態資訊。</param>
    /// <exception cref="ArgumentNullException">當 <paramref name="sample"/> 或 <paramref name="configuration"/> 為 null 時擲出。</exception>
    public AacSampleReadEventArgs(EncodedAudioSample sample, AacCodecConfiguration configuration)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Data = sample.Data;
        PresentationTimestamp = sample.PresentationTimestamp;
        DecodeTimestamp = sample.DecodeTimestamp;
        Duration = sample.Duration;
    }

    /// <summary>
    /// 取得 AAC 音訊樣本資料的防禦性複製。
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// 取得顯示時間標記 (Presentation Timestamp, PTS)。
    /// </summary>
    public TimeSpan PresentationTimestamp { get; }

    /// <summary>
    /// 取得解碼時間標記 (Decode Timestamp, DTS)。
    /// </summary>
    public TimeSpan DecodeTimestamp { get; }

    /// <summary>
    /// 取得樣本持續時間。
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// 取得對應的 AAC 音訊編解碼器組態資訊。
    /// </summary>
    public AacCodecConfiguration Configuration { get; }

    /// <summary>
    /// 取得音訊取樣率 (Hz)。
    /// </summary>
    public int SampleRate => Configuration.SampleRate;

    /// <summary>
    /// 取得聲道配置數 (1~7)。
    /// </summary>
    public int ChannelConfiguration => Configuration.ChannelConfiguration;

    /// <summary>
    /// 取得 MPEG-4 AudioSpecificConfig 二進位標頭的防禦性複製。
    /// </summary>
    public byte[] AudioSpecificConfig => Configuration.AudioSpecificConfig;

    /// <summary>
    /// 取得顯示時間標記 (PTS) 別名。
    /// </summary>
    public TimeSpan Pts => PresentationTimestamp;

    /// <summary>
    /// 取得解碼時間標記 (DTS) 別名。
    /// </summary>
    public TimeSpan Dts => DecodeTimestamp;
}

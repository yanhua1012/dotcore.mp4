using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

/// <summary>
/// 同步與非同步 MP4 寫入二進位一致性 (Parity) 矩陣測試套件。
/// </summary>
public sealed class WriterAsyncParityMatrixTests
{
    public static IEnumerable<object[]> Matrix
    {
        get
        {
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
            {
                foreach (var layout in new[]
                         {
                             Mp4WriteMode.Progressive,
                             Mp4WriteMode.FastStart,
                             Mp4WriteMode.Fragmented
                         })
                {
                    yield return new object[] { codec, layout };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task PureAsyncAndSequentialMixedMatchSyncBytesAndRoundTrip(VideoCodec codec, Mp4WriteMode layout)
    {
        var configuration = VideoConfigurationFor(codec);
        var syncBytes = BuildSync(configuration, layout);
        var asyncBytes = await BuildPureAsync(configuration, layout);
        var mixedBytes = await BuildSequentialMixed(configuration, layout);

        var syncHash = Sha256(syncBytes);
        Assert.Equal(syncHash, Sha256(asyncBytes));
        Assert.Equal(syncHash, Sha256(mixedBytes));

        AssertRoundTrip(syncBytes, codec, expectedVideo: 3, expectedAudio: 2);
        AssertRoundTrip(asyncBytes, codec, expectedVideo: 3, expectedAudio: 2);
        AssertRoundTrip(mixedBytes, codec, expectedVideo: 3, expectedAudio: 2);
    }

    private static void AssertRoundTrip(byte[] bytes, VideoCodec codec, int expectedVideo, int expectedAudio)
    {
        using var reader = new Mp4Reader(new MemoryStream(bytes, writable: false));
        Assert.NotNull(reader.VideoConfiguration);
        Assert.Equal(codec, reader.VideoConfiguration!.Codec);
        Assert.Equal(expectedVideo, reader.ReadVideoNalUnits().Count());
        Assert.Equal(expectedAudio, reader.ReadAudioSamples().Count());
    }

    private static VideoCodecConfiguration VideoConfigurationFor(VideoCodec codec)
    {
        return codec == VideoCodec.H264
            ? VideoCodecConfiguration.CreateH264(new byte[] { 0x67, 0x42, 0x00, 0x1e }, new byte[] { 0x68, 0xce, 0x06, 0xe2 }, 4, 16, 16)
            : VideoCodecConfiguration.CreateH265(new byte[] { 0x40, 0x01 }, new byte[] { 0x42, 0x01 }, new byte[] { 0x44, 0x01 }, 4, 16, 16);
    }

    // Shared deterministic sample sequence, ordered by non-decreasing global DTS so it is
    // valid for progressive, faststart and fragmented (keyframe-first) layouts alike.
    private static readonly IReadOnlyList<Sample> Samples = new[]
    {
        new Sample(true, new byte[] { 0x65, 0x01 }, TimeSpan.Zero, TimeSpan.Zero, true),
        new Sample(false, new byte[] { 0x21, 0x10 }, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), true),
        new Sample(true, new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), false),
        new Sample(false, new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60), true),
        new Sample(true, new byte[] { 0x65, 0x03 }, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80), true),
    };

    private static byte[] BuildSync(VideoCodecConfiguration configuration, Mp4WriteMode layout)
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = layout };
        using (var writer = new Mp4Writer(output, options))
        {
            writer.SetVideoCodecConfiguration(configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            foreach (var sample in Samples)
            {
                if (sample.Video)
                {
                    writer.WriteVideoNalUnit(new EncodedVideoNalUnit(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(40), sample.KeyFrame));
                }
                else
                {
                    writer.WriteAudioSample(new EncodedAudioSample(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(20)));
                }
            }
            writer.FinalizeFile();
        }
        return output.ToArray();
    }

    private static async Task<byte[]> BuildPureAsync(VideoCodecConfiguration configuration, Mp4WriteMode layout)
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = layout };
        using (var writer = await Mp4Writer.CreateAsync(output, options).ConfigureAwait(false))
        {
            writer.SetVideoCodecConfiguration(configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            foreach (var sample in Samples)
            {
                if (sample.Video)
                {
                    await writer.WriteVideoNalUnitAsync(new EncodedVideoNalUnit(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(40), sample.KeyFrame)).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAudioSampleAsync(new EncodedAudioSample(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(20))).ConfigureAwait(false);
                }
            }
            await writer.FinalizeFileAsync().ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> BuildSequentialMixed(VideoCodecConfiguration configuration, Mp4WriteMode layout)
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = layout };
        using (var writer = await Mp4Writer.CreateAsync(output, options).ConfigureAwait(false))
        {
            writer.SetVideoCodecConfiguration(configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            for (var index = 0; index < Samples.Count; index++)
            {
                var sample = Samples[index];
                if (index % 2 == 0)
                {
                    if (sample.Video)
                    {
                        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(40), sample.KeyFrame));
                    }
                    else
                    {
                        writer.WriteAudioSample(new EncodedAudioSample(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(20)));
                    }
                }
                else
                {
                    if (sample.Video)
                    {
                        await writer.WriteVideoNalUnitAsync(new EncodedVideoNalUnit(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(40), sample.KeyFrame)).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteAudioSampleAsync(new EncodedAudioSample(sample.Data, sample.Pts, sample.Dts, TimeSpan.FromMilliseconds(20))).ConfigureAwait(false);
                    }
                }
            }
            await writer.FinalizeFileAsync().ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static string Sha256(byte[] data)
    {
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(data));
    }

    private sealed record Sample(bool Video, byte[] Data, TimeSpan Pts, TimeSpan Dts, bool KeyFrame);
}
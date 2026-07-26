using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// Fragmented MP4 Reader 功能測試套件。
/// </summary>
public sealed class FragmentedReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderRestoresFragmentedCodecPayloadTimingFlagsAndCrossTrackOrder(bool h265)
    {
        var bytes = WriteMixedFragmented(h265);
        using var input = new MemoryStream(bytes);
        using var reader = new Mp4Reader(input);

        var video = reader.ReadVideoNalUnits().ToArray();
        var audio = reader.ReadAudioSamples().ToArray();
        Assert.Equal(h265 ? VideoCodec.H265 : VideoCodec.H264, reader.VideoConfiguration!.Codec);
        Assert.Equal(TestMedia.AacConfiguration.AudioSpecificConfig, reader.AudioConfiguration!.AudioSpecificConfig);
        Assert.Equal(3, video.Length);
        Assert.Equal(new[] { true, false, true }, video.Select(sample => sample.IsKeyFrame));
        Assert.Equal(
            new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80) },
            video.Select(sample => sample.DecodeTimestamp));
        Assert.Equal(5, audio.Length);
        Assert.Equal(new byte[] { 0x21, 60 }, audio[3].Data);

        var events = new List<string>();
        reader.VideoNalUnitRead += (_, sample) => events.Add("v:" + sample.DecodeTimestamp.TotalMilliseconds);
        reader.AacSampleRead += (_, sample) => events.Add("a:" + sample.DecodeTimestamp.TotalMilliseconds);
        reader.Read();
        Assert.Equal(new[] { "v:0", "a:0", "a:20", "v:40", "a:40", "a:60", "v:80", "a:80" }, events);
    }

    [Fact]
    public void ReaderSplitsEveryNalInAFragmentedAccessUnit()
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, true);
            WriteVideo(writer, new byte[] { 0x06, 0x02 }, 0, true);
            writer.FinalizeFile();
        }

        using var reader = new Mp4Reader(new MemoryStream(output.ToArray()));
        var samples = reader.ReadVideoNalUnits().ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Equal(new byte[] { 0x65, 0x01 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x06, 0x02 }, samples[1].Data);
        Assert.Equal(samples[0].DecodeTimestamp, samples[1].DecodeTimestamp);
    }

    [Fact]
    public void ReaderRestoresSignedNegativeCompositionOffset()
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                new byte[] { 0x65, 0x01 },
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(40),
                true));
            writer.FinalizeFile();
        }

        using var reader = new Mp4Reader(new MemoryStream(output.ToArray()));
        var sample = reader.ReadVideoNalUnits().Single();
        Assert.Equal(TimeSpan.Zero, sample.PresentationTimestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(40), sample.DecodeTimestamp);
    }

    [Fact]
    public void ReaderContinuesDataAndDecodeTimeAcrossMultipleTruns()
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, true);
            WriteVideo(writer, new byte[] { 0x41, 0x02 }, 40, false);
            writer.FinalizeFile();
        }

        var bytes = SplitSingleVideoTrun(output.ToArray());
        using var reader = new Mp4Reader(new MemoryStream(bytes));
        var samples = reader.ReadVideoNalUnits().ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Equal(new byte[] { 0x65, 0x01 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x41, 0x02 }, samples[1].Data);
        Assert.Equal(TimeSpan.FromMilliseconds(40), samples[1].DecodeTimestamp);
        Assert.False(samples[1].IsKeyFrame);
    }

    [Fact]
    public void ReaderRejectsAmbiguousProgressiveAndFragmentedSamples()
    {
        var bytes = WriteMixedFragmented(false);
        var stsz = FindBoxStarts(bytes, "stsz").First();
        WriteUInt32(bytes, stsz + 16, 1);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsUnknownFragmentTrackMapping()
    {
        var bytes = WriteMixedFragmented(false);
        var tfhd = FindBoxStarts(bytes, "tfhd").First();
        WriteUInt32(bytes, tfhd + 12, 99);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("unknown track", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsMutuallyExclusiveFragmentFlags()
    {
        var tfhdBytes = WriteMixedFragmented(false);
        var tfhd = FindBoxStarts(tfhdBytes, "tfhd").First();
        tfhdBytes[tfhd + 11] |= 1;
        var tfhdError = Assert.Throws<Mp4FormatException>(() =>
            new Mp4Reader(new MemoryStream(tfhdBytes)));
        Assert.Contains("must not both", tfhdError.Message, StringComparison.OrdinalIgnoreCase);

        var trunBytes = WriteMixedFragmented(false);
        var trun = FindBoxStarts(trunBytes, "trun").First();
        trunBytes[trun + 11] |= 4;
        var trunError = Assert.Throws<Mp4FormatException>(() =>
            new Mp4Reader(new MemoryStream(trunBytes)));
        Assert.Contains("must not both", trunError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsMissingTfdt()
    {
        var bytes = WriteMixedFragmented(false);
        var tfdt = FindBoxStarts(bytes, "tfdt").First();
        Encoding.ASCII.GetBytes("free").CopyTo(bytes, tfdt + 4);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("tfdt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsNonMonotonicFragmentSequence()
    {
        var bytes = WriteMixedFragmented(false);
        var mfhd = FindBoxStarts(bytes, "mfhd").ToArray();
        Assert.Equal(2, mfhd.Length);
        WriteUInt32(bytes, mfhd[1] + 12, 1);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("sequence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsDuplicateTrackMappingWithinFragment()
    {
        var bytes = WriteMixedFragmented(false);
        var firstMoof = FindBoxStarts(bytes, "moof").First();
        var nextMdat = FindBoxStarts(bytes, "mdat").First(start => start > firstMoof);
        var tfhd = FindBoxStarts(bytes, "tfhd")
            .Where(start => start > firstMoof && start < nextMdat)
            .ToArray();
        Assert.Equal(2, tfhd.Length);
        WriteUInt32(bytes, tfhd[1] + 12, 1);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("same supported track", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsFragmentTimelineRegression()
    {
        var bytes = WriteMixedFragmented(false);
        var videoTfdt = FindBoxStarts(bytes, "tfdt").ToArray();
        Assert.True(videoTfdt.Length >= 3);
        WriteUInt64(bytes, videoTfdt[2] + 12, 0);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("regress", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsSampleRangeOutsideFollowingMdat()
    {
        var bytes = WriteMixedFragmented(false);
        var trun = FindBoxStarts(bytes, "trun").First();
        WriteUInt32(bytes, trun + 16, 0);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("mdat", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsOverlappingTrackRunRanges()
    {
        var bytes = WriteMixedFragmented(false);
        var firstMoof = FindBoxStarts(bytes, "moof").First();
        var nextMdat = FindBoxStarts(bytes, "mdat").First(start => start > firstMoof);
        var trun = FindBoxStarts(bytes, "trun")
            .Where(start => start > firstMoof && start < nextMdat)
            .ToArray();
        Assert.Equal(2, trun.Length);
        WriteUInt32(bytes, trun[1] + 16, ReadUInt32(bytes, trun[0] + 16));

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsFragmentSampleExpansionBeforeIteration()
    {
        var bytes = WriteMixedFragmented(false);
        var trun = FindBoxStarts(bytes, "trun").First();
        WriteUInt32(bytes, trun + 12, (uint)Mp4Reader.MaximumSampleCount + 1);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("sample", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsTruncatedFinalFragment()
    {
        var bytes = WriteMixedFragmented(false);
        Array.Resize(ref bytes, bytes.Length - 1);

        Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));
    }

    private static byte[] WriteMixedFragmented(bool h265)
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(h265 ? TestMedia.H265Configuration : TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            WriteVideo(writer, h265 ? new byte[] { 0x26, 0x01 } : new byte[] { 0x65, 0x01 }, 0, true);
            WriteAudio(writer, 0);
            WriteAudio(writer, 20);
            WriteVideo(writer, h265 ? new byte[] { 0x02, 0x01 } : new byte[] { 0x41, 0x02 }, 40, false);
            WriteAudio(writer, 40);
            WriteAudio(writer, 60);
            WriteVideo(writer, h265 ? new byte[] { 0x26, 0x03 } : new byte[] { 0x65, 0x03 }, 80, true);
            WriteAudio(writer, 80);
            writer.FinalizeFile();
        }

        return output.ToArray();
    }

    private static Mp4Writer CreateWriter(Stream output)
    {
        return new Mp4Writer(output, new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented });
    }

    private static void WriteVideo(Mp4Writer writer, byte[] data, int milliseconds, bool keyframe)
    {
        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
            data,
            TimeSpan.FromMilliseconds(milliseconds),
            TimeSpan.FromMilliseconds(milliseconds),
            TimeSpan.FromMilliseconds(40),
            keyframe));
    }

    private static void WriteAudio(Mp4Writer writer, int milliseconds)
    {
        writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, (byte)milliseconds }, TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static IEnumerable<int> FindBoxStarts(byte[] bytes, string type)
    {
        var marker = Encoding.ASCII.GetBytes(type);
        for (var offset = 4; offset <= bytes.Length - 4; offset++)
        {
            if (!bytes.Skip(offset).Take(4).SequenceEqual(marker)) continue;
            var start = offset - 4;
            var size = ReadUInt32(bytes, start);
            if (size >= 8 && start <= bytes.Length - size) yield return start;
        }
    }

    private static byte[] SplitSingleVideoTrun(byte[] source)
    {
        var moof = FindBoxStarts(source, "moof").Single();
        var traf = FindBoxStarts(source, "traf").Single();
        var trun = FindBoxStarts(source, "trun").Single();
        var originalSize = checked((int)ReadUInt32(source, trun));
        Assert.Equal(2U, ReadUInt32(source, trun + 12));
        var originalDataOffset = unchecked((int)ReadUInt32(source, trun + 16));

        var first = new byte[36];
        Buffer.BlockCopy(source, trun, first, 0, 20);
        WriteUInt32(first, 0, (uint)first.Length);
        WriteUInt32(first, 12, 1);
        WriteUInt32(first, 16, unchecked((uint)(originalDataOffset + 16)));
        Buffer.BlockCopy(source, trun + 20, first, 20, 16);

        var second = new byte[32];
        WriteUInt32(second, 0, (uint)second.Length);
        Encoding.ASCII.GetBytes("trun").CopyTo(second, 4);
        second[8] = source[trun + 8];
        second[9] = source[trun + 9];
        second[10] = source[trun + 10];
        second[11] = (byte)(source[trun + 11] & ~1);
        WriteUInt32(second, 12, 1);
        Buffer.BlockCopy(source, trun + 36, second, 16, 16);

        var delta = first.Length + second.Length - originalSize;
        var result = new byte[source.Length + delta];
        Buffer.BlockCopy(source, 0, result, 0, trun);
        Buffer.BlockCopy(first, 0, result, trun, first.Length);
        Buffer.BlockCopy(second, 0, result, trun + first.Length, second.Length);
        Buffer.BlockCopy(
            source,
            trun + originalSize,
            result,
            trun + first.Length + second.Length,
            source.Length - trun - originalSize);
        WriteUInt32(result, traf, checked(ReadUInt32(source, traf) + (uint)delta));
        WriteUInt32(result, moof, checked(ReadUInt32(source, moof) + (uint)delta));
        return result;
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static void WriteUInt64(byte[] bytes, int offset, ulong value)
    {
        WriteUInt32(bytes, offset, (uint)(value >> 32));
        WriteUInt32(bytes, offset + 4, (uint)value);
    }
}

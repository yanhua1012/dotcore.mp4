using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// FastStart 模式寫入器與 Offset 校正測試套件。
/// </summary>
public sealed class FastStartWriterTests
{
    [Fact]
    public void AdjustedOffsetsUseCheckedArithmeticAndPromoteAtStcoBoundary()
    {
        Assert.Equal(uint.MaxValue, FastStartLayout.AdjustOffset(uint.MaxValue - 10L, 10));
        Assert.False(SampleTableDecisions.RequiresCo64(new[] { (long)uint.MaxValue }));

        var promoted = FastStartLayout.AdjustOffset(uint.MaxValue - 10L, 11);
        Assert.True(promoted > uint.MaxValue);
        Assert.True(SampleTableDecisions.RequiresCo64(new[] { promoted }));
        Assert.Throws<Mp4FormatException>(() => FastStartLayout.AdjustOffset(long.MaxValue, 1));
    }

    [Fact]
    public void MovieLengthConvergenceRepeatsUntilOffsetEncodingIsStable()
    {
        var adjustments = new List<long>();

        var result = FastStartLayout.BuildStableMovieBox(adjustment =>
        {
            adjustments.Add(adjustment);
            return new byte[adjustment < 100 ? 100 : adjustment < 108 ? 108 : 108];
        });

        Assert.Equal(new long[] { 0, 100, 108 }, adjustments);
        Assert.Equal(108, result.Length);
    }

    [Fact]
    public void FastStartMovesMoovBeforeMdatAndPreservesPayloadAndOffsets()
    {
        using var output = new MemoryStream();
        using var writer = new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart });
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.Zero, TimeSpan.Zero));
        writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        writer.FinalizeFile();
        var firstLength = output.Length;
        writer.Complete();
        writer.Finish();

        var bytes = output.ToArray();
        var topLevel = ReadBoxes(bytes, 0, bytes.Length);
        Assert.Equal(new[] { "ftyp", "moov", "mdat" }, topLevel.Select(box => box.Type));
        Assert.Equal(firstLength, output.Length);

        var mdat = topLevel.Single(box => box.Type == "mdat");
        Assert.Equal(
            new byte[] { 0x21, 0x10, 0, 0, 0, 2, 0x65, 0x01 },
            bytes.Skip(mdat.PayloadOffset).Take(mdat.End - mdat.PayloadOffset));

        var chunkOffsets = ReadChunkOffsets(bytes);
        Assert.Equal(2, chunkOffsets.Count);
        Assert.All(chunkOffsets, offset => Assert.InRange(offset, (long)mdat.PayloadOffset, (long)mdat.End - 1));
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0x65, 0x01 }, bytes.Skip((int)chunkOffsets[0]).Take(6));
        Assert.Equal(new byte[] { 0x21, 0x10 }, bytes.Skip((int)chunkOffsets[1]).Take(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastStartRoundTripsVideoAudioTimingAndOwnership(bool h265)
    {
        var configuration = h265 ? TestMedia.H265Configuration : TestMedia.H264Configuration;
        var nal = h265 ? new byte[] { 0x26, 0x01 } : new byte[] { 0x65, 0x01 };
        var output = new TrackingMemoryStream();
        using (var writer = new Mp4Writer(
                   output,
                   new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart }))
        {
            writer.SetVideoCodecConfiguration(configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(
                nal,
                TimeSpan.FromMilliseconds(40),
                TimeSpan.Zero));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        Assert.False(output.Closed);
        using var reader = new Mp4Reader(output);
        var video = reader.ReadVideoNalUnits().Single();
        var audio = reader.ReadAudioSamples().Single();
        Assert.Equal(configuration.Codec, reader.VideoConfiguration!.Codec);
        Assert.Equal(nal, video.Data);
        Assert.Equal(TimeSpan.FromMilliseconds(40), video.PresentationTimestamp);
        Assert.Equal(TimeSpan.Zero, video.DecodeTimestamp);
        Assert.True(video.IsKeyFrame);
        Assert.Equal(new byte[] { 0x21, 0x10 }, audio.Data);
        Assert.Equal(TimeSpan.FromMilliseconds(20), audio.Duration);
    }

    [Fact]
    public void FastStartRelocationReadFailureIsReported()
    {
        using var output = new FailingReadMemoryStream();
        using var writer = new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart });
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
        output.FailReads = true;

        var error = Assert.Throws<IOException>(() => writer.FinalizeFile());

        Assert.Contains("relocat", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<long> ReadChunkOffsets(byte[] bytes)
    {
        var offsets = new List<long>();
        for (var index = 4; index <= bytes.Length - 4; index++)
        {
            var type = Encoding.ASCII.GetString(bytes, index, 4);
            if (type != "stco" && type != "co64") continue;
            var start = index - 4;
            var size = checked((int)ReadUInt32(bytes, start));
            if (size < 16 || start > bytes.Length - size) continue;
            var count = checked((int)ReadUInt32(bytes, start + 12));
            var entrySize = type == "co64" ? 8 : 4;
            if (16 + checked(count * entrySize) != size) continue;
            for (var item = 0; item < count; item++)
            {
                var offset = start + 16 + item * entrySize;
                offsets.Add(type == "co64"
                    ? checked((long)ReadUInt64(bytes, offset))
                    : ReadUInt32(bytes, offset));
            }
        }

        return offsets;
    }

    private static IReadOnlyList<TestBox> ReadBoxes(byte[] bytes, int start, int end)
    {
        var result = new List<TestBox>();
        var offset = start;
        while (offset < end)
        {
            Assert.True(end - offset >= 8);
            var size32 = ReadUInt32(bytes, offset);
            var headerSize = size32 == 1 ? 16 : 8;
            var size = size32 == 1
                ? checked((int)ReadUInt64(bytes, offset + 8))
                : checked((int)size32);
            Assert.InRange(size, headerSize, end - offset);
            result.Add(new TestBox(
                Encoding.ASCII.GetString(bytes, offset + 4, 4),
                offset,
                offset + headerSize,
                offset + size));
            offset += size;
        }

        Assert.Equal(end, offset);
        return result;
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        return ((ulong)ReadUInt32(bytes, offset) << 32) | ReadUInt32(bytes, offset + 4);
    }

    private sealed class TestBox
    {
        public TestBox(string type, int start, int payloadOffset, int end)
        {
            Type = type;
            Start = start;
            PayloadOffset = payloadOffset;
            End = end;
        }

        public string Type { get; }
        public int Start { get; }
        public int PayloadOffset { get; }
        public int End { get; }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadMemoryStream : MemoryStream
    {
        public bool FailReads { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (FailReads) throw new IOException("Injected read failure.");
            return base.Read(buffer, offset, count);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class FragmentedWriterTests
{
    [Fact]
    public void InitialMovieAndFragmentsContainRequiredBoxesAndKeyframeBoundaries()
    {
        using var output = new MemoryStream();
        using var writer = CreateWriter(output);
        ConfigureMixed(writer, TestMedia.H264Configuration);

        WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, 0, true);
        WriteAudio(writer, 0);
        WriteAudio(writer, 20);
        WriteVideo(writer, new byte[] { 0x41, 0x02 }, 40, 40, false);
        WriteAudio(writer, 40);
        WriteAudio(writer, 60);
        WriteVideo(writer, new byte[] { 0x65, 0x03 }, 80, 80, true);
        WriteAudio(writer, 80);
        writer.FinalizeFile();
        var firstLength = output.Length;
        writer.Complete();

        var bytes = output.ToArray();
        var topLevel = ReadTopLevelBoxes(bytes);
        Assert.Equal(new[] { "ftyp", "moov", "moof", "mdat", "moof", "mdat" }, topLevel.Select(box => box.Type));
        Assert.Equal(firstLength, output.Length);
        Assert.Contains(FindBoxStarts(bytes, "mvex"), _ => true);
        Assert.Equal(2, FindBoxStarts(bytes, "trex").Count);
        Assert.All(FindBoxStarts(bytes, "stsz"), start => Assert.Equal(0U, ReadUInt32(bytes, start + 16)));

        var moofs = topLevel.Where(box => box.Type == "moof").ToArray();
        Assert.Equal(1U, ReadUInt32(bytes, FindWithin(bytes, moofs[0], "mfhd") + 12));
        Assert.Equal(2U, ReadUInt32(bytes, FindWithin(bytes, moofs[1], "mfhd") + 12));
        Assert.All(FindBoxStarts(bytes, "tfhd"), start =>
        {
            var flags = ReadUInt24(bytes, start + 9);
            Assert.NotEqual(0U, flags & 0x020000U);
        });

        var videoTfdt = FindWithin(bytes, moofs[1], "tfdt");
        Assert.Equal((ulong)TimeSpan.FromMilliseconds(80).Ticks, ReadUInt64(bytes, videoTfdt + 12));
        AssertFragmentRanges(bytes, topLevel);
    }

    [Fact]
    public void FragmentCompositionOffsetsUseUnsignedAndSignedTrunVersions()
    {
        using var positiveOutput = new MemoryStream();
        using (var writer = CreateWriter(positiveOutput))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H265Configuration);
            WriteVideo(writer, new byte[] { 0x26, 0x01 }, 40, 0, true);
            writer.FinalizeFile();
        }

        Assert.Equal(0, positiveOutput.ToArray()[FindBoxStarts(positiveOutput.ToArray(), "trun").Single() + 8]);

        using var negativeOutput = new MemoryStream();
        using (var writer = CreateWriter(negativeOutput))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, 40, true);
            writer.FinalizeFile();
        }

        Assert.Equal(1, negativeOutput.ToArray()[FindBoxStarts(negativeOutput.ToArray(), "trun").Single() + 8]);
    }

    [Fact]
    public void FragmentedWriterFreezesTracksAndRejectsNonKeyframeStart()
    {
        using var output = new MemoryStream();
        using var writer = CreateWriter(output);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);

        var nonKeyError = Assert.Throws<InvalidOperationException>(() =>
            WriteVideo(writer, new byte[] { 0x41, 0x01 }, 0, 0, false));
        Assert.Contains("keyframe", nonKeyError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToArray());

        WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, 0, true);
        Assert.Throws<InvalidOperationException>(() => writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration));
        Assert.Throws<InvalidOperationException>(() => writer.SetVideoCodecConfiguration(TestMedia.H264Configuration));
    }

    [Fact]
    public void FragmentedWriterRejectsAudioWithoutVideoBeforeWritingAnything()
    {
        using var output = new MemoryStream();
        using var writer = CreateWriter(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var error = Assert.Throws<InvalidOperationException>(() => WriteAudio(writer, 0));

        Assert.Contains("video", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void FragmentedWriterEnforcesGlobalDtsButAllowsSameDtsAcrossTracks()
    {
        using var output = new MemoryStream();
        using var writer = CreateWriter(output);
        ConfigureMixed(writer, TestMedia.H264Configuration);
        WriteVideo(writer, new byte[] { 0x65, 0x01 }, 40, 40, true);
        WriteAudio(writer, 40);
        var length = output.Length;

        var error = Assert.Throws<Mp4TimestampException>(() => WriteAudio(writer, 20));

        Assert.Contains("audio", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(length, output.Length);
        writer.FinalizeFile();
    }

    [Fact]
    public void FragmentBufferLimitFailsBeforeHeaderOrNonKeyframeFragment()
    {
        using var output = new MemoryStream();
        using var writer = new Mp4Writer(
            output,
            new Mp4WriterOptions
            {
                Mode = Mp4WriteMode.Fragmented,
                MaximumFragmentBufferBytes = 5
            });
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);

        var error = Assert.Throws<InvalidOperationException>(() =>
            WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, 0, true));

        Assert.Contains("buffer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void FragmentedWriterCommitsSequentiallyToNonSeekableOutput()
    {
        using var output = new NonSeekableWriteStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            WriteVideo(writer, new byte[] { 0x65, 0x01 }, 0, 0, true);
            WriteVideo(writer, new byte[] { 0x65, 0x02 }, 40, 40, true);
            writer.FinalizeFile();
        }

        Assert.Equal(
            new[] { "ftyp", "moov", "moof", "mdat", "moof", "mdat" },
            ReadTopLevelBoxes(output.Bytes).Select(box => box.Type));
    }

    private static Mp4Writer CreateWriter(Stream output)
    {
        return new Mp4Writer(output, new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented });
    }

    private static void ConfigureMixed(Mp4Writer writer, VideoCodecConfiguration video)
    {
        writer.SetVideoCodecConfiguration(video);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
    }

    private static void WriteVideo(Mp4Writer writer, byte[] data, int ptsMilliseconds, int dtsMilliseconds, bool keyframe)
    {
        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
            data,
            TimeSpan.FromMilliseconds(ptsMilliseconds),
            TimeSpan.FromMilliseconds(dtsMilliseconds),
            TimeSpan.FromMilliseconds(40),
            keyframe));
    }

    private static void WriteAudio(Mp4Writer writer, int milliseconds)
    {
        writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, (byte)milliseconds }, TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static void AssertFragmentRanges(byte[] bytes, IReadOnlyList<TestBox> topLevel)
    {
        for (var index = 0; index < topLevel.Count; index++)
        {
            if (topLevel[index].Type != "moof") continue;
            var moof = topLevel[index];
            var mdat = topLevel[index + 1];
            Assert.Equal("mdat", mdat.Type);
            foreach (var trunStart in FindBoxStarts(bytes, "trun").Where(start => start >= moof.Start && start < moof.End))
            {
                var flags = ReadUInt24(bytes, trunStart + 9);
                Assert.NotEqual(0U, flags & 1U);
                var sampleCount = checked((int)ReadUInt32(bytes, trunStart + 12));
                var dataOffset = ReadInt32(bytes, trunStart + 16);
                var cursor = trunStart + 20;
                long payloadSize = 0;
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    if ((flags & 0x000100) != 0) cursor += 4;
                    if ((flags & 0x000200) != 0)
                    {
                        payloadSize += ReadUInt32(bytes, cursor);
                        cursor += 4;
                    }

                    if ((flags & 0x000400) != 0) cursor += 4;
                    if ((flags & 0x000800) != 0) cursor += 4;
                }

                var rangeStart = checked((long)moof.Start + dataOffset);
                Assert.InRange(rangeStart, mdat.PayloadOffset, mdat.End);
                Assert.InRange(rangeStart + payloadSize, mdat.PayloadOffset, mdat.End);
            }
        }
    }

    private static IReadOnlyList<TestBox> ReadTopLevelBoxes(byte[] bytes)
    {
        var result = new List<TestBox>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size = checked((int)ReadUInt32(bytes, offset));
            Assert.InRange(size, 8, bytes.Length - offset);
            result.Add(new TestBox(
                Encoding.ASCII.GetString(bytes, offset + 4, 4),
                offset,
                offset + 8,
                offset + size));
            offset += size;
        }

        Assert.Equal(bytes.Length, offset);
        return result;
    }

    private static IReadOnlyList<int> FindBoxStarts(byte[] bytes, string type)
    {
        var result = new List<int>();
        var marker = Encoding.ASCII.GetBytes(type);
        for (var offset = 4; offset <= bytes.Length - 4; offset++)
        {
            if (!bytes.Skip(offset).Take(4).SequenceEqual(marker)) continue;
            var start = offset - 4;
            var size = ReadUInt32(bytes, start);
            if (size >= 8 && start <= bytes.Length - size) result.Add(start);
        }

        return result;
    }

    private static int FindWithin(byte[] bytes, TestBox parent, string type)
    {
        return FindBoxStarts(bytes, type).First(start => start >= parent.PayloadOffset && start < parent.End);
    }

    private static uint ReadUInt24(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 16) | ((uint)bytes[offset + 1] << 8) | bytes[offset + 2];
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private static int ReadInt32(byte[] bytes, int offset) => unchecked((int)ReadUInt32(bytes, offset));

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

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();
        public byte[] Bytes => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}

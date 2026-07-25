using System;
using System.IO;
using System.Linq;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class ReaderErrorTests
{
    [Fact]
    public void ReaderRejectsMalformedLengthPrefixedVideoSampleDuringRead()
    {
        var bytes = CreateVideoFile();
        var mdat = Find(bytes, "mdat");
        bytes[mdat + 12] = 0x7f;
        bytes[mdat + 13] = 0xff;
        bytes[mdat + 14] = 0xff;
        bytes[mdat + 15] = 0xff;

        using var reader = new Mp4Reader(new MemoryStream(bytes));
        Assert.Throws<Mp4FormatException>(() => reader.ReadVideoNalUnits().ToArray());
    }

    [Fact]
    public void ReaderRejectsUnsupportedVideoSampleEntry()
    {
        var bytes = CreateVideoFile();
        var sampleEntry = Find(bytes, "avc1");
        bytes[sampleEntry] = (byte)'v';
        bytes[sampleEntry + 1] = (byte)'p';
        bytes[sampleEntry + 2] = (byte)'0';
        bytes[sampleEntry + 3] = (byte)'9';
        Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));
    }

    [Fact]
    public void ReaderPropagatesEventHandlerExceptionAndLeavesCallerStreamOpen()
    {
        var bytes = CreateVideoFile();
        using var stream = new MemoryStream(bytes);
        using var reader = new Mp4Reader(stream);
        reader.VideoNalUnitRead += (_, _) => throw new InvalidOperationException("handler failure");
        Assert.Throws<InvalidOperationException>(() => reader.ReadVideoNalUnits().ToArray());
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ChunkOffsetSelectionUsesStcoUntilA64BitOffsetIsRequired()
    {
        Assert.False(SampleTableDecisions.RequiresCo64(new[] { 0L, (long)uint.MaxValue }));
        Assert.True(SampleTableDecisions.RequiresCo64(new[] { (long)uint.MaxValue + 1 }));
    }

    private static byte[] CreateVideoFile()
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.Zero, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        return stream.ToArray();
    }

    private static int Find(byte[] data, string text)
    {
        var value = new byte[] { (byte)text[0], (byte)text[1], (byte)text[2], (byte)text[3] };
        for (var i = 0; i <= data.Length - value.Length; i++)
        {
            if (data[i] == value[0] && data[i + 1] == value[1] && data[i + 2] == value[2] && data[i + 3] == value[3]) return i;
        }

        throw new InvalidOperationException("Fixture box not found: " + text);
    }
}

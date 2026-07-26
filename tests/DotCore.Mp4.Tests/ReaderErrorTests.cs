using System;
using System.IO;
using System.Linq;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// MP4 讀取器格式錯誤與例外處理測試套件。
/// </summary>
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
    public void ReaderWrapsMalformedAudioSpecificConfigAsFormatException()
    {
        var bytes = CreateAudioFile(new byte[] { 0x12, 0x10 });
        var esds = Find(bytes, "esds");
        var asc = Find(bytes, new byte[] { 0x12, 0x10 }, esds);
        bytes[asc] = 0;
        bytes[asc + 1] = 0;

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("AudioSpecificConfig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderWrapsMislabeledVideoParameterSetAsFormatException()
    {
        var bytes = CreateVideoFile();
        var avcConfiguration = Find(bytes, "avcC");
        bytes[avcConfiguration + 12] = 0x68;

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("codec configuration", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsMismatchedSttsAndStszSampleCounts()
    {
        var bytes = CreateVideoFile();
        WriteUInt32(bytes, Find(bytes, "stts") + 12, 2);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("stts", error.Message, StringComparison.Ordinal);
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

    [Fact]
    public void ChunkOffsetConversionRejectsValuesOutsideTheReaderRange()
    {
        Assert.Equal((long)uint.MaxValue, SampleTableDecisions.ToReaderChunkOffset(uint.MaxValue));
        Assert.Throws<Mp4FormatException>(() => SampleTableDecisions.ToReaderChunkOffset(ulong.MaxValue));
    }

    [Fact]
    public void SampleToChunkEntriesMustStartAtOneAndStrictlyIncrease()
    {
        SampleTableDecisions.ValidateSampleToChunkFirstChunks(new uint[] { 1, 3, 7 });
        Assert.Throws<Mp4FormatException>(() =>
            SampleTableDecisions.ValidateSampleToChunkFirstChunks(new uint[] { 2 }));
        Assert.Throws<Mp4FormatException>(() =>
            SampleTableDecisions.ValidateSampleToChunkFirstChunks(new uint[] { 1, 1 }));
        Assert.Throws<Mp4FormatException>(() =>
            SampleTableDecisions.ValidateSampleToChunkFirstChunks(new uint[] { 1, 4, 3 }));
    }

    [Fact]
    public void ReaderRejectsDuplicateSupportedTrackBeforeParsingItsSampleTables()
    {
        var bytes = CreateVideoFile();
        var moovStart = Find(bytes, "moov") - 4;
        var moovSize = checked((int)ReadUInt32(bytes, moovStart));
        var trackStart = Find(bytes, "trak") - 4;
        var trackSize = checked((int)ReadUInt32(bytes, trackStart));
        Assert.Equal(bytes.Length, moovStart + moovSize);

        var duplicate = new byte[bytes.Length + trackSize];
        Buffer.BlockCopy(bytes, 0, duplicate, 0, bytes.Length);
        Buffer.BlockCopy(bytes, trackStart, duplicate, bytes.Length, trackSize);
        WriteUInt32(duplicate, moovStart, checked((uint)(moovSize + trackSize)));
        WriteUInt32(duplicate, Find(duplicate, "stsz", bytes.Length) + 12, uint.MaxValue);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(duplicate)));

        Assert.Contains("more than one supported video track", error.Message, StringComparison.Ordinal);
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

    private static byte[] CreateAudioFile(byte[] audioSpecificConfig)
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetAudioCodecConfiguration(new AacCodecConfiguration(audioSpecificConfig, 44100, 2));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        return stream.ToArray();
    }

    private static int Find(byte[] data, string text)
    {
        return Find(data, text, 0);
    }

    private static int Find(byte[] data, string text, int start)
    {
        var value = new byte[] { (byte)text[0], (byte)text[1], (byte)text[2], (byte)text[3] };
        for (var i = start; i <= data.Length - value.Length; i++)
        {
            if (data[i] == value[0] && data[i + 1] == value[1] && data[i + 2] == value[2] && data[i + 3] == value[3]) return i;
        }

        throw new InvalidOperationException("Fixture box not found: " + text);
    }

    private static int Find(byte[] data, byte[] value, int start)
    {
        for (var i = start; i <= data.Length - value.Length; i++)
        {
            var match = true;
            for (var j = 0; j < value.Length; j++) match &= data[i + j] == value[j];
            if (match) return i;
        }

        throw new InvalidOperationException("Fixture byte sequence not found.");
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }
}

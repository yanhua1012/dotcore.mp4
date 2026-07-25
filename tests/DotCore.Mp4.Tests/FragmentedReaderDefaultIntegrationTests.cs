using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class FragmentedReaderDefaultIntegrationTests
{
    private const uint DefaultBaseIsMoof = 0x020000;
    private const uint DefaultDurationPresent = 0x000008;
    private const uint DefaultSizePresent = 0x000010;
    private const uint DefaultFlagsPresent = 0x000020;
    private const uint DataOffsetPresent = 0x000001;
    private const uint FirstSampleFlagsPresent = 0x000004;
    private const uint SampleDurationPresent = 0x000100;
    private const uint SampleSizePresent = 0x000200;
    private const uint SampleFlagsPresent = 0x000400;
    private const uint NonSyncSample = 0x00010000;

    [Theory]
    [InlineData(DefaultSource.Trun, 300_000U, false)]
    [InlineData(DefaultSource.Tfhd, 200_000U, false)]
    [InlineData(DefaultSource.Trex, 100_000U, false)]
    public void ReaderResolvesFragmentValuesByTrunTfhdTrexPrecedence(
        DefaultSource source,
        uint expectedDuration,
        bool expectedKeyframe)
    {
        var bytes = BuildSingleTrackFragment(
            source == DefaultSource.Trex ? 100_000U : 90_000U,
            source == DefaultSource.Trex ? 6U : 8U,
            source == DefaultSource.Trex ? NonSyncSample : 0U,
            source == DefaultSource.Tfhd || source == DefaultSource.Trun ? 200_000U : null,
            source == DefaultSource.Trun ? 7U : source == DefaultSource.Tfhd ? 6U : null,
            source == DefaultSource.Tfhd ? NonSyncSample : source == DefaultSource.Trun ? 0U : null,
            source == DefaultSource.Trun ? 300_000U : null,
            source == DefaultSource.Trun ? 6U : null,
            source == DefaultSource.Trun ? NonSyncSample : null,
            null,
            new[] { VideoPayload(0x65, 0x01) });

        using var reader = new Mp4Reader(new MemoryStream(bytes));
        var sample = reader.ReadVideoNalUnits().Single();

        Assert.Equal(new byte[] { 0x65, 0x01 }, sample.Data);
        Assert.Equal(TimeSpan.FromTicks(expectedDuration), sample.Duration);
        Assert.Equal(expectedKeyframe, sample.IsKeyFrame);
    }

    [Fact]
    public void ReaderAppliesFirstSampleFlagsOnlyToFirstSampleBeforeTrackDefaults()
    {
        var bytes = BuildSingleTrackFragment(
            0,
            0,
            0,
            400_000,
            6,
            0,
            null,
            null,
            null,
            NonSyncSample,
            new[] { VideoPayload(0x65, 0x01), VideoPayload(0x41, 0x02) });

        using var reader = new Mp4Reader(new MemoryStream(bytes));
        var samples = reader.ReadVideoNalUnits().ToArray();

        Assert.Equal(new[] { false, true }, samples.Select(sample => sample.IsKeyFrame));
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(40) }, samples.Select(sample => sample.DecodeTimestamp));
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40) }, samples.Select(sample => sample.Duration));
        Assert.Equal(new byte[] { 0x65, 0x01 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x41, 0x02 }, samples[1].Data);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("size")]
    public void ReaderRejectsUnresolvedFragmentValue(string field)
    {
        var bytes = BuildSingleTrackFragment(
            0,
            0,
            0,
            field == "duration" ? null : 400_000U,
            field == "size" ? null : 6U,
            0,
            null,
            null,
            null,
            null,
            new[] { VideoPayload(0x65, 0x01) });

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trun", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tfhd", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trex", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildSingleTrackFragment(
        uint trexDuration,
        uint trexSize,
        uint trexFlags,
        uint? tfhdDuration,
        uint? tfhdSize,
        uint? tfhdFlags,
        uint? trunDuration,
        uint? trunSize,
        uint? trunFlags,
        uint? firstSampleFlags,
        IReadOnlyList<byte[]> payloads)
    {
        var initial = WriteInitialMovie();
        var trex = FindBoxStarts(initial, "trex").Single();
        WriteUInt32(initial, trex + 20, trexDuration);
        WriteUInt32(initial, trex + 24, trexSize);
        WriteUInt32(initial, trex + 28, trexFlags);

        var tfhdFieldValues = new List<byte[]> { U32(1) };
        var tfhdBoxFlags = DefaultBaseIsMoof;
        AddOptionalField(tfhdDuration, DefaultDurationPresent, ref tfhdBoxFlags, tfhdFieldValues);
        AddOptionalField(tfhdSize, DefaultSizePresent, ref tfhdBoxFlags, tfhdFieldValues);
        AddOptionalField(tfhdFlags, DefaultFlagsPresent, ref tfhdBoxFlags, tfhdFieldValues);

        var trunFieldValues = new List<byte[]> { U32((uint)payloads.Count), U32(0) };
        var trunBoxFlags = DataOffsetPresent;
        if (firstSampleFlags.HasValue)
        {
            trunBoxFlags |= FirstSampleFlagsPresent;
            trunFieldValues.Add(U32(firstSampleFlags.Value));
        }

        for (var index = 0; index < payloads.Count; index++)
        {
            AddOptionalField(trunDuration, SampleDurationPresent, ref trunBoxFlags, trunFieldValues, index == 0);
            AddOptionalField(trunSize, SampleSizePresent, ref trunBoxFlags, trunFieldValues, index == 0);
            AddOptionalField(trunFlags, SampleFlagsPresent, ref trunBoxFlags, trunFieldValues, index == 0);
        }

        var moof = BuildMoof(tfhdBoxFlags, tfhdFieldValues, trunBoxFlags, trunFieldValues);
        WriteUInt32(moof, FindBoxStarts(moof, "trun").Single() + 16, checked((uint)moof.Length + 8));
        var mdat = Box("mdat", payloads.SelectMany(value => value).ToArray());
        return initial.Concat(moof).Concat(mdat).ToArray();
    }

    private static byte[] BuildMoof(
        uint tfhdFlags,
        IReadOnlyList<byte[]> tfhdFields,
        uint trunFlags,
        IReadOnlyList<byte[]> trunFields)
    {
        return Box(
            "moof",
            FullBox("mfhd", 0, 0, U32(1)),
            Box(
                "traf",
                FullBox("tfhd", 0, tfhdFlags, tfhdFields.ToArray()),
                FullBox("tfdt", 1, 0, U64(0)),
                FullBox("trun", 0, trunFlags, trunFields.ToArray())));
    }

    private static void AddOptionalField(
        uint? value,
        uint flag,
        ref uint flags,
        ICollection<byte[]> fields,
        bool addFlag = true)
    {
        if (!value.HasValue) return;
        if (addFlag) flags |= flag;
        fields.Add(U32(value.Value));
    }

    private static byte[] WriteInitialMovie()
    {
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(
                   output,
                   new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented }))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.Zero, TimeSpan.Zero));
        }

        return output.ToArray();
    }

    private static byte[] VideoPayload(byte first, byte second)
    {
        return new byte[] { 0, 0, 0, 2, first, second };
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

    private static byte[] FullBox(string type, byte version, uint flags, params byte[][] fields)
    {
        var payload = new List<byte>
        {
            version,
            (byte)(flags >> 16),
            (byte)(flags >> 8),
            (byte)flags
        };
        foreach (var field in fields) payload.AddRange(field);
        return Box(type, payload.ToArray());
    }

    private static byte[] Box(string type, params byte[][] payloads)
    {
        var length = 8 + payloads.Sum(payload => payload.Length);
        var result = new List<byte>(length);
        result.AddRange(U32((uint)length));
        result.AddRange(Encoding.ASCII.GetBytes(type));
        foreach (var payload in payloads) result.AddRange(payload);
        return result.ToArray();
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

    private static byte[] U32(uint value)
    {
        return new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        };
    }

    private static byte[] U64(ulong value)
    {
        return U32((uint)(value >> 32)).Concat(U32((uint)value).ToArray()).ToArray();
    }

    public enum DefaultSource
    {
        Trun,
        Tfhd,
        Trex
    }
}

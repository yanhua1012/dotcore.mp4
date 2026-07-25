using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class FragmentedReaderResourceLimitTests
{
    [Fact]
    public void ReaderRetainsGenericContainerBoxLimit()
    {
        var bytes = new List<byte>(WriteInitialMovie());
        var emptyFree = Box("free");
        for (var index = 0; index <= Mp4Reader.MaximumBoxesPerContainer; index++)
        {
            bytes.AddRange(emptyFree);
        }

        var error = Assert.Throws<Mp4FormatException>(() =>
            new Mp4Reader(new MemoryStream(bytes.ToArray())));

        Assert.Contains("container", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("box count", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsExcessiveTopLevelFragments()
    {
        var bytes = new List<byte>(WriteInitialMovie());
        var emptyMoof = Box("moof");
        for (var index = 0; index <= Mp4Reader.MaximumFragmentCount; index++)
        {
            bytes.AddRange(emptyMoof);
        }
        bytes.Add(0xff);

        var error = Assert.Throws<Mp4FormatException>(() =>
            new Mp4Reader(new MemoryStream(bytes.ToArray())));

        Assert.Contains("fragment count", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsExcessiveTrafsBeforeParsingThem()
    {
        var children = new List<byte>();
        children.AddRange(FullBox("mfhd", 0, 0, U32(1)));
        var emptyTraf = Box("traf");
        for (var index = 0; index <= Mp4Reader.MaximumTrackFragmentsPerFragment; index++)
        {
            children.AddRange(emptyTraf);
        }
        children.Add(0xff);

        var bytes = new List<byte>(WriteInitialMovie());
        bytes.AddRange(Box("moof", children.ToArray()));
        bytes.AddRange(Box("mdat"));

        var error = Assert.Throws<Mp4FormatException>(() =>
            new Mp4Reader(new MemoryStream(bytes.ToArray())));

        Assert.Contains("traf count", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsExcessiveTrunsBeforeExpandingSamples()
    {
        var trafChildren = new List<byte>();
        trafChildren.AddRange(FullBox("tfhd", 0, 0x020000, U32(1)));
        trafChildren.AddRange(FullBox("tfdt", 1, 0, U64(0)));
        var emptyTrun = Box("trun");
        for (var index = 0; index <= Mp4Reader.MaximumTrackRunsPerFragment; index++)
        {
            trafChildren.AddRange(emptyTrun);
        }
        trafChildren.Add(0xff);

        var moofChildren = new List<byte>();
        moofChildren.AddRange(FullBox("mfhd", 0, 0, U32(1)));
        moofChildren.AddRange(Box("traf", trafChildren.ToArray()));
        var bytes = new List<byte>(WriteInitialMovie());
        bytes.AddRange(Box("moof", moofChildren.ToArray()));
        bytes.AddRange(Box("mdat"));

        var error = Assert.Throws<Mp4FormatException>(() =>
            new Mp4Reader(new MemoryStream(bytes.ToArray())));

        Assert.Contains("trun count", error.Message, StringComparison.OrdinalIgnoreCase);
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
            return output.ToArray();
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
        return U32((uint)(value >> 32)).Concat(U32((uint)value)).ToArray();
    }
}

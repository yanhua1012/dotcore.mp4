using System.IO;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class IsoBmffWriterTests
{
    [Fact]
    public void PrimitiveWriterUsesBigEndianAndBackpatchesBoxLength()
    {
        using var stream = new MemoryStream();
        var writer = new IsoBmffWriter(stream);
        var box = writer.BeginBox("test");
        writer.WriteUInt16(0x1234);
        writer.WriteUInt32(0x89abcdef);
        writer.EndBox(box);

        Assert.Equal(new byte[] { 0, 0, 0, 0x0e, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0x12, 0x34, 0x89, 0xab, 0xcd, 0xef }, stream.ToArray());
    }

    [Fact]
    public void FourCharacterBoxTypeIsRequired()
    {
        using var stream = new MemoryStream();
        var writer = new IsoBmffWriter(stream);
        Assert.Throws<System.ArgumentException>(() => writer.WriteFourCc("abc"));
        Assert.Throws<System.ArgumentException>(() => writer.WriteFourCc("éééé"));
    }

    [Fact]
    public void BoxWriterRejectsOversizedTypeWithoutEmittingPayload()
    {
        using var stream = new MemoryStream();
        var writer = new IsoBmffWriter(stream);
        var box = writer.BeginBox("free");
        writer.WriteUInt64(0x0102030405060708UL);
        writer.EndBox(box);
        Assert.Equal(16, stream.Length);
        Assert.Equal(16, stream.ToArray()[3]);
    }
}

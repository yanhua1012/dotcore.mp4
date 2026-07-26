using System;
using System.IO;
using System.Text;

namespace DotCore.Mp4;

internal sealed class IsoBmffWriter
{
    private readonly Stream _stream;

    public IsoBmffWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public long Position => _stream.Position;

    public long BeginBox(string type)
    {
        var start = Position;
        WriteUInt32(0);
        WriteFourCc(type);
        return start;
    }

    public void EndBox(long start)
    {
        var size = checked(Position - start);
        if (size > uint.MaxValue)
        {
            throw new Mp4FormatException("The MP4 box exceeds the 32-bit box-size limit.");
        }

        var end = Position;
        _stream.Seek(start, SeekOrigin.Begin);
        WriteUInt32((uint)size);
        _stream.Seek(end, SeekOrigin.Begin);
    }

    public void WriteFourCc(string type)
    {
        if (type == null || type.Length != 4)
        {
            throw new ArgumentException("An ISO BMFF box type must contain exactly four characters.", nameof(type));
        }

        for (var i = 0; i < type.Length; i++)
        {
            if (type[i] > 0x7f)
            {
                throw new ArgumentException("An ISO BMFF box type must contain ASCII characters.", nameof(type));
            }

            _stream.WriteByte((byte)type[i]);
        }
    }

    public void WriteUInt8(byte value) => _stream.WriteByte(value);

    public void WriteUInt16(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

    public void WriteUInt32(uint value)
    {
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    public void WriteUInt32(long value)
    {
        if (value < 0 || value > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        WriteUInt32((uint)value);
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt64(ulong value)
    {
        WriteUInt32((uint)(value >> 32));
        WriteUInt32((uint)value);
    }

    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public void WriteFixed16_16(double value)
    {
        WriteInt32(checked((int)Math.Round(value * 65536.0, MidpointRounding.AwayFromZero)));
    }

    public void WriteBytes(byte[] value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        _stream.Write(value, 0, value.Length);
    }

    public void WriteBytes(byte[] value, int offset, int count)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (offset < 0 || offset > value.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0 || count > value.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
        _stream.Write(value, offset, count);
    }

    public void WriteZeros(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        for (var i = 0; i < count; i++) _stream.WriteByte(0);
    }

    public static byte[] FourCcBytes(string type)
    {
        using (var stream = new MemoryStream())
        {
            var writer = new IsoBmffWriter(stream);
            writer.WriteFourCc(type);
            return stream.ToArray();
        }
    }

    public static byte[] BuildBox(string type, Action<IsoBmffWriter> body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        using (var stream = new MemoryStream())
        {
            var writer = new IsoBmffWriter(stream);
            var start = writer.BeginBox(type);
            body(writer);
            writer.EndBox(start);
            return stream.ToArray();
        }
    }
}

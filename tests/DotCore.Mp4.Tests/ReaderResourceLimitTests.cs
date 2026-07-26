using System;
using System.IO;
using System.Reflection;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// MP4 Reader 資源限制防護測試套件。
/// </summary>
public sealed class ReaderResourceLimitTests
{
    [Fact]
    public void ReaderRejectsInputAboveSnapshotLimitBeforeReading()
    {
        var limit = GetReaderLimit("MaximumInputBytes");
        using var stream = new DeclaredLengthStream(limit + 1);

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(stream));

        Assert.Contains("input", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(stream.ReadCalled);
    }

    [Fact]
    public void ReaderRejectsSampleCountAboveLimitBeforeExpansion()
    {
        var limit = GetReaderLimit("MaximumSampleCount");
        var bytes = CreateAudioFile(new byte[] { 0x12, 0x10 });
        WriteUInt32(bytes, Find(bytes, "stsz") + 12, checked((uint)limit + 1));

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("sample count", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReaderRejectsDescriptorNestingAboveLimit()
    {
        var limit = checked((int)GetReaderLimit("MaximumDescriptorDepth"));
        var audioSpecificConfig = new byte[96];
        audioSpecificConfig[0] = 0x12;
        audioSpecificConfig[1] = 0x10;
        var bytes = CreateAudioFile(audioSpecificConfig);
        var esds = Find(bytes, "esds");
        var descriptorStart = esds + 8;
        var wrapperCount = limit + 1;
        var cursor = descriptorStart;
        for (var i = 0; i < wrapperCount; i++)
        {
            bytes[cursor++] = 0x06;
            bytes[cursor++] = checked((byte)(((wrapperCount - i - 1) * 2) + 4));
        }

        bytes[cursor++] = 0x05;
        bytes[cursor++] = 2;
        bytes[cursor++] = 0x12;
        bytes[cursor] = 0x10;

        var error = Assert.Throws<Mp4FormatException>(() => new Mp4Reader(new MemoryStream(bytes)));

        Assert.Contains("depth", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static long GetReaderLimit(string name)
    {
        var field = typeof(Mp4Reader).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Convert.ToInt64(field!.GetRawConstantValue());
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
        var value = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i <= data.Length - value.Length; i++)
        {
            var match = true;
            for (var j = 0; j < value.Length; j++) match &= data[i + j] == value[j];
            if (match) return i;
        }

        throw new InvalidOperationException("Fixture box not found: " + text);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private sealed class DeclaredLengthStream : Stream
    {
        private long _position;

        public DeclaredLengthStream(long length)
        {
            Length = length;
        }

        public bool ReadCalled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalled = true;
            throw new InvalidOperationException("The reader must reject the declared length before reading.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

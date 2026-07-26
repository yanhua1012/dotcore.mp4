using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// Writer 寫入模式與預設選項契約測試套件。
/// </summary>
public sealed class WriterModeContractTests
{
    [Fact]
    public void WriterModesAndOptionsExposeSafeDefaults()
    {
        Assert.Equal(new[] { "Progressive", "FastStart", "Fragmented" }, Enum.GetNames<Mp4WriteMode>());

        var options = new Mp4WriterOptions();

        Assert.Equal(Mp4WriteMode.Progressive, options.Mode);
        Assert.True(options.MaximumFragmentBufferBytes > 0);
    }

    [Fact]
    public void LegacyConstructorStillProducesProgressiveLayout()
    {
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        Assert.Equal(new[] { "ftyp", "mdat", "moov" }, ReadTopLevelBoxTypes(output.ToArray()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidMaximumFragmentBufferIsRejectedBeforeHeader(int maximumBytes)
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { MaximumFragmentBufferBytes = maximumBytes };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Mp4Writer(output, options));

        Assert.Equal(nameof(Mp4WriterOptions.MaximumFragmentBufferBytes), error.ParamName);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void UnknownModeIsRejectedBeforeHeader()
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = (Mp4WriteMode)int.MaxValue };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Mp4Writer(output, options));

        Assert.Equal(nameof(Mp4WriterOptions.Mode), error.ParamName);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void WriterSnapshotsCallerOptions()
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Progressive };
        using var writer = new Mp4Writer(output, options);
        options.Mode = Mp4WriteMode.Fragmented;
        options.MaximumFragmentBufferBytes = 1;

        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
        writer.FinalizeFile();

        Assert.Equal(new[] { "ftyp", "mdat", "moov" }, ReadTopLevelBoxTypes(output.ToArray()));
    }

    [Fact]
    public void ProgressiveRejectsNonSeekableOutputBeforeHeader()
    {
        using var output = new CapabilityStream(canRead: false, canSeek: false, canSetLength: true);

        Assert.Throws<InvalidOperationException>(() => new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.Progressive }));

        Assert.Empty(output.Bytes);
    }

    [Fact]
    public void FastStartRejectsUnreadableOutputBeforeHeader()
    {
        using var output = new CapabilityStream(canRead: false, canSeek: true, canSetLength: true);

        var error = Assert.Throws<InvalidOperationException>(() => new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart }));

        Assert.Contains("readable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.Bytes);
    }

    [Fact]
    public void FastStartRejectsOutputWithoutSetLengthBeforeHeader()
    {
        using var output = new CapabilityStream(canRead: true, canSeek: true, canSetLength: false);

        var error = Assert.Throws<InvalidOperationException>(() => new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart }));

        Assert.Contains("length", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.Bytes);
    }

    [Fact]
    public void FragmentedAcceptsWritableNonSeekableOutputWithoutWritingHeader()
    {
        using var output = new CapabilityStream(canRead: false, canSeek: false, canSetLength: false);
        using var writer = new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented });

        Assert.Empty(output.Bytes);
    }

    [Theory]
    [InlineData(Mp4WriteMode.Progressive)]
    [InlineData(Mp4WriteMode.FastStart)]
    [InlineData(Mp4WriteMode.Fragmented)]
    public void EveryModeHonorsLeaveOpenWhenDisposed(Mp4WriteMode mode)
    {
        var output = new CapabilityStream(canRead: true, canSeek: true, canSetLength: true);

        using (new Mp4Writer(output, new Mp4WriterOptions { Mode = mode }, leaveOpen: false))
        {
        }

        Assert.True(output.Closed);
    }

    private static string[] ReadTopLevelBoxTypes(byte[] bytes)
    {
        var result = new List<string>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size32 = ReadUInt32(bytes, offset);
            var size = size32 == 1
                ? checked((int)ReadUInt64(bytes, offset + 8))
                : checked((int)size32);
            Assert.InRange(size, size32 == 1 ? 16 : 8, int.MaxValue);
            result.Add(Encoding.ASCII.GetString(bytes, offset + 4, 4));
            offset = checked(offset + size);
        }

        Assert.Equal(bytes.Length, offset);
        return result.ToArray();
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

    private sealed class CapabilityStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();
        private readonly bool _canRead;
        private readonly bool _canSeek;
        private readonly bool _canSetLength;

        public CapabilityStream(bool canRead, bool canSeek, bool canSetLength)
        {
            _canRead = canRead;
            _canSeek = canSeek;
            _canSetLength = canSetLength;
        }

        public byte[] Bytes => _inner.ToArray();
        public bool Closed { get; private set; }
        public override bool CanRead => _canRead;
        public override bool CanSeek => _canSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set
            {
                if (!_canSeek) throw new NotSupportedException();
                _inner.Position = value;
            }
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_canRead) throw new NotSupportedException();
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_canSeek) throw new NotSupportedException();
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            if (!_canSetLength) throw new NotSupportedException();
            _inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

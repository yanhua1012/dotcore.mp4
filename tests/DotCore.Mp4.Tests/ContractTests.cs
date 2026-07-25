using System;
using System.IO;
using System.Linq;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class ContractTests
{
    [Fact]
    public void VideoConfigurationCopiesParameterSetsAndRequiresAllH265Sets()
    {
        var vps = new byte[] { 0x40, 0x01 };
        var sps = new byte[] { 0x42, 0x01 };
        var pps = new byte[] { 0x44, 0x01 };
        var configuration = VideoCodecConfiguration.CreateH265(vps, sps, pps);

        vps[0] = 0;
        Assert.Equal(0x40, configuration.Vps[0]);
        Assert.Throws<ArgumentException>(() => VideoCodecConfiguration.CreateH265(Array.Empty<byte>(), sps, pps));
        Assert.Throws<ArgumentNullException>(() => VideoCodecConfiguration.CreateH265(null!, sps, pps));
        Assert.Throws<ArgumentNullException>(() => VideoCodecConfiguration.CreateH264(null!, pps));
    }

    [Fact]
    public void VideoConfigurationRejectsMislabeledParameterSetNalTypes()
    {
        Assert.Throws<ArgumentException>(() => VideoCodecConfiguration.CreateH264(
            new byte[] { 0x68, 0x01 },
            new byte[] { 0x68, 0x02 }));
        Assert.Throws<ArgumentException>(() => VideoCodecConfiguration.CreateH264(
            new byte[] { 0x67, 0x01 },
            new byte[] { 0x67, 0x02 }));
        Assert.Throws<ArgumentException>(() => VideoCodecConfiguration.CreateH265(
            new byte[] { 0x42, 0x01 },
            new byte[] { 0x42, 0x02 },
            new byte[] { 0x44, 0x01 }));
        Assert.Throws<ArgumentException>(() => VideoCodecConfiguration.CreateH265(
            new byte[] { 0x40, 0x01 },
            new byte[] { 0x44, 0x02 },
            new byte[] { 0x44, 0x01 }));
        Assert.Throws<ArgumentException>(() => VideoCodecConfiguration.CreateH265(
            new byte[] { 0x40, 0x01 },
            new byte[] { 0x42, 0x02 },
            new byte[] { 0x42, 0x01 }));
    }

    [Fact]
    public void AacConfigurationRequiresAscToMatchDeclaredParameters()
    {
        var configuration = new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);
        Assert.Equal(2, configuration.AudioObjectType);
        Assert.Equal(44100, configuration.SampleRate);
        Assert.Equal(2, configuration.ChannelConfiguration);
        Assert.Throws<ArgumentException>(() => new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 48000, 2));
        Assert.Throws<ArgumentException>(() => AacCodecConfiguration.FromAudioSpecificConfig(new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void UnsupportedOutputStreamIsRejectedBeforeAnyHeader()
    {
        using var stream = new NonSeekableWriteStream();
        Assert.Throws<InvalidOperationException>(() => new Mp4Writer(stream));
        Assert.Empty(stream.Bytes);
    }

    [Fact]
    public void WriterRejectsNullAndNonWritableOutputBeforeAnyHeader()
    {
        Assert.Throws<ArgumentNullException>(() => new Mp4Writer(null!));

        using var stream = new MemoryStream(Array.Empty<byte>(), false);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<InvalidOperationException>(() => new Mp4Writer(stream));
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void TimestampConversionRejectsInexactScaleAndPreservesExactScale()
    {
        Assert.Equal(90L, MediaTime.ToTicks(TimeSpan.FromMilliseconds(1), 90000));
        Assert.Throws<Mp4TimestampException>(() => MediaTime.ToTicks(TimeSpan.FromTicks(1), 90000));
        Assert.Equal(TimeSpan.FromMilliseconds(1), MediaTime.FromTicks(90, 90000));
    }

    [Fact]
    public void WriterRejectsDecreasingDecodeTimestamp()
    {
        using var stream = new MemoryStream();
        using var writer = new Mp4Writer(stream);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        Assert.Throws<Mp4TimestampException>(() => writer.WriteVideoNalUnit(
            TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.Zero, TimeSpan.Zero)));
    }

    [Fact]
    public void WriterRejectsDecreasingAudioDecodeTimestampBeforeWritingSample()
    {
        using var stream = new MemoryStream();
        using var writer = new Mp4Writer(stream);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        var duration = TimeSpan.FromMilliseconds(20);
        writer.WriteAudioSample(new EncodedAudioSample(
            new byte[] { 0x21, 0x10 },
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            duration));
        var length = stream.Length;

        var error = Assert.Throws<Mp4TimestampException>(() => writer.WriteAudioSample(
            new EncodedAudioSample(new byte[] { 0x22, 0x10 }, TimeSpan.Zero, TimeSpan.Zero, duration)));

        Assert.Contains("audio DTS", error.Message, StringComparison.Ordinal);
        Assert.Equal(length, stream.Length);
    }

    [Fact]
    public void WriterDoesNotCloseCallerOwnedStream()
    {
        var stream = new TrackingMemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        Assert.False(stream.Closed);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void WriterAndReaderCloseCallerStreamsWhenLeaveOpenIsFalse()
    {
        var output = new TrackingMemoryStream();
        using (var writer = new Mp4Writer(output, false))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        Assert.True(output.Closed);
        var input = new TrackingMemoryStream(output.Bytes);
        using (var reader = new Mp4Reader(input, false))
        {
            Assert.NotNull(reader.AudioConfiguration);
        }

        Assert.True(input.Closed);
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();
        public byte[] Bytes => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public TrackingMemoryStream()
        {
        }

        public TrackingMemoryStream(byte[] bytes) : base(bytes)
        {
        }

        public bool Closed { get; private set; }
        public byte[] Bytes => ToArray();

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }
}

internal static class TestMedia
{
    public static readonly VideoCodecConfiguration H264Configuration =
        VideoCodecConfiguration.CreateH264(new byte[] { 0x67, 0x42, 0x00, 0x1e }, new byte[] { 0x68, 0xce, 0x06, 0xe2 }, 4, 16, 16);

    public static readonly VideoCodecConfiguration H265Configuration =
        VideoCodecConfiguration.CreateH265(
            new byte[] { 0x40, 0x01 },
            new byte[] { 0x42, 0x01 },
            new byte[] { 0x44, 0x01 },
            4,
            16,
            16);

    public static readonly AacCodecConfiguration AacConfiguration =
        new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);

    public static EncodedVideoNalUnit Video(byte[] data, TimeSpan pts, TimeSpan dts, bool isKeyFrame = true)
    {
        return new EncodedVideoNalUnit(data, pts, dts, TimeSpan.FromMilliseconds(40), isKeyFrame);
    }

    public static EncodedAudioSample Audio(byte[] data, TimeSpan pts)
    {
        return new EncodedAudioSample(data, pts, pts, TimeSpan.FromMilliseconds(20));
    }
}

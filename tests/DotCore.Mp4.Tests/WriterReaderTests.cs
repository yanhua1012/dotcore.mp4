using System;
using System.IO;
using System.Linq;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class WriterReaderTests
{
    [Fact]
    public void AnnexBNalsWithEqualTimestampsBecomeOneSampleAndReadInOrder()
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                new byte[] { 0, 0, 0, 1, 0x65, 0x01 },
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(40),
                true));
            writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                new byte[] { 0, 0, 1, 0x06, 0x05 },
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(40),
                true));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40)));
            writer.FinalizeFile();
        }

        var events = 0;
        using var reader = new Mp4Reader(stream);
        reader.VideoNalUnitRead += (_, _) => events++;
        var samples = reader.ReadVideoNalUnits().ToArray();
        Assert.Equal(3, samples.Length);
        Assert.Equal(3, events);
        Assert.Equal(new byte[] { 0x65, 0x01 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x06, 0x05 }, samples[1].Data);
        Assert.Equal(TimeSpan.FromMilliseconds(40), samples[2].PresentationTimestamp);
        Assert.True(samples[0].IsKeyFrame);
    }

    [Fact]
    public void AudioSamplesRemainStableAndCallerStreamStaysOpen()
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x01, 0x02 }, TimeSpan.Zero));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x03, 0x04 }, TimeSpan.FromMilliseconds(20)));
            writer.FinalizeFile();
        }

        using var reader = new Mp4Reader(stream);
        var events = 0;
        reader.AacSampleRead += (_, _) => events++;
        var samples = reader.ReadAudioSamples().ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Equal(2, events);
        Assert.Equal(new byte[] { 0x01, 0x02 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x03, 0x04 }, samples[1].Data);
        Assert.Equal(44100, reader.AudioConfiguration!.SampleRate);
        Assert.Equal(2, reader.AudioConfiguration.ChannelConfiguration);
        Assert.True(stream.CanRead);
    }
}

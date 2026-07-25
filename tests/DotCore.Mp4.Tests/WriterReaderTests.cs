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
    public void H265NalsWithEqualTimestampsBecomeOneSampleAndReadInOrder()
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H265Configuration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x26, 0x01 }, TimeSpan.Zero, TimeSpan.Zero));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x4e, 0x01 }, TimeSpan.Zero, TimeSpan.Zero));
            writer.WriteVideoNalUnit(TestMedia.Video(
                new byte[] { 0x02, 0x01 },
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(40),
                false));
            writer.FinalizeFile();
        }

        var bytes = stream.ToArray();
        Assert.Equal(2U, ReadUInt32(bytes, Find(bytes, "stsz") + 12));

        using var reader = new Mp4Reader(stream);
        var samples = reader.ReadVideoNalUnits().ToArray();
        Assert.Equal(3, samples.Length);
        Assert.Equal(new byte[] { 0x26, 0x01 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x4e, 0x01 }, samples[1].Data);
        Assert.Equal(samples[0].PresentationTimestamp, samples[1].PresentationTimestamp);
        Assert.Equal(samples[0].DecodeTimestamp, samples[1].DecodeTimestamp);
        Assert.Equal(samples[0].Duration, samples[1].Duration);
        Assert.Equal(samples[0].IsKeyFrame, samples[1].IsKeyFrame);
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
        var events = new System.Collections.Generic.List<AacSampleReadEventArgs>();
        reader.AacSampleRead += (_, value) => events.Add(value);
        var samples = reader.ReadAudioSamples().ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Equal(2, events.Count);
        Assert.Equal(new byte[] { 0x01, 0x02 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x03, 0x04 }, samples[1].Data);
        Assert.Equal(44100, reader.AudioConfiguration!.SampleRate);
        Assert.Equal(2, reader.AudioConfiguration.ChannelConfiguration);
        Assert.Equal(samples[0].Data, events[0].Data);
        Assert.Equal(samples[0].PresentationTimestamp, events[0].PresentationTimestamp);
        Assert.Equal(samples[0].DecodeTimestamp, events[0].DecodeTimestamp);
        Assert.Equal(samples[0].Duration, events[0].Duration);
        Assert.Equal(44100, events[0].SampleRate);
        Assert.Equal(2, events[0].ChannelConfiguration);
        Assert.Equal(new byte[] { 0x12, 0x10 }, events[0].AudioSpecificConfig);
        Assert.True(stream.CanRead);
    }

    private static int Find(byte[] data, string text)
    {
        var value = System.Text.Encoding.ASCII.GetBytes(text);
        for (var i = 0; i <= data.Length - value.Length; i++)
        {
            if (data.Skip(i).Take(value.Length).SequenceEqual(value)) return i;
        }

        throw new InvalidOperationException("Fixture box not found: " + text);
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }
}

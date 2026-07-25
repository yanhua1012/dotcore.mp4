using System;

namespace DotCore.Mp4;

internal sealed class ParsedAacConfig
{
    public int AudioObjectType { get; set; }
    public int SampleRate { get; set; }
    public int ChannelConfiguration { get; set; }
}

internal static class AacConfigParser
{
    private static readonly int[] SampleRates =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350
    };

    public static ParsedAacConfig Parse(byte[] value)
    {
        if (value == null || value.Length < 2)
        {
            throw new ArgumentException("AudioSpecificConfig must contain at least two bytes.", nameof(value));
        }

        var reader = new BitReader(value);
        var audioObjectType = ReadAudioObjectType(reader);
        var sampleRate = ReadSampleRate(reader);
        var channelConfiguration = (int)reader.ReadBits(4);

        if (audioObjectType < 1 || audioObjectType > 31)
        {
            throw new ArgumentException("AudioSpecificConfig contains an invalid audio object type.", nameof(value));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentException("AudioSpecificConfig contains an invalid sample rate.", nameof(value));
        }

        if (channelConfiguration < 1 || channelConfiguration > 7)
        {
            throw new ArgumentException("AudioSpecificConfig contains an unsupported channel configuration.", nameof(value));
        }

        return new ParsedAacConfig
        {
            AudioObjectType = audioObjectType,
            SampleRate = sampleRate,
            ChannelConfiguration = channelConfiguration
        };
    }

    private static int ReadAudioObjectType(BitReader reader)
    {
        var value = (int)reader.ReadBits(5);
        if (value == 31)
        {
            value = 32 + (int)reader.ReadBits(6);
        }

        return value;
    }

    private static int ReadSampleRate(BitReader reader)
    {
        var index = (int)reader.ReadBits(4);
        if (index == 15)
        {
            return (int)reader.ReadBits(24);
        }

        if (index < 0 || index >= SampleRates.Length)
        {
            throw new ArgumentException("AudioSpecificConfig contains an invalid sample-rate index.");
        }

        return SampleRates[index];
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _position;

        public BitReader(byte[] data)
        {
            _data = data;
        }

        public uint ReadBits(int count)
        {
            if (count < 0 || count > 24 || _position + count > _data.Length * 8)
            {
                throw new ArgumentException("AudioSpecificConfig ended before all required fields were present.");
            }

            uint result = 0;
            for (var i = 0; i < count; i++)
            {
                var byteIndex = _position / 8;
                var bitIndex = 7 - (_position % 8);
                result = (result << 1) | (uint)((_data[byteIndex] >> bitIndex) & 1);
                _position++;
            }

            return result;
        }
    }
}

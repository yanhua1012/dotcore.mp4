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

    public static byte[] Encode(int sampleRate, int channelConfiguration, int audioObjectType = 2)
    {
        if (audioObjectType < 1 || audioObjectType > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(audioObjectType), "Audio object type must be between 1 and 31.");
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be greater than zero.");
        }

        if (channelConfiguration < 1 || channelConfiguration > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(channelConfiguration), "Only explicit MPEG-4 channel configurations one through seven are supported.");
        }

        var sampleRateIndex = Array.IndexOf(SampleRates, sampleRate);

        var writer = new BitWriter();
        writer.WriteBits((uint)audioObjectType, 5);

        if (sampleRateIndex >= 0)
        {
            writer.WriteBits((uint)sampleRateIndex, 4);
        }
        else
        {
            writer.WriteBits(15, 4);
            writer.WriteBits((uint)sampleRate, 24);
        }

        writer.WriteBits((uint)channelConfiguration, 4);

        return writer.ToByteArray();
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

    private sealed class BitWriter
    {
        private byte[] _buffer;
        private int _bitPosition;

        public BitWriter(int initialByteCapacity = 8)
        {
            _buffer = new byte[initialByteCapacity];
        }

        public void WriteBits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                var bit = (byte)((value >> i) & 1);
                var byteIndex = _bitPosition / 8;
                var bitIndex = 7 - (_bitPosition % 8);
                if (byteIndex >= _buffer.Length)
                {
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                }

                _buffer[byteIndex] |= (byte)(bit << bitIndex);
                _bitPosition++;
            }
        }

        public byte[] ToByteArray()
        {
            var byteLength = (_bitPosition + 7) / 8;
            var result = new byte[byteLength];
            Buffer.BlockCopy(_buffer, 0, result, 0, byteLength);
            return result;
        }
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

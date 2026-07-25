using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotCore.Mp4;

namespace DotCore.Mp4.IntegrationTests;

internal static class FixtureData
{
    private const string H264AnnexBBase64 =
        "AAAAAWdCwArd7ARAAAADAEAAAAyjxIngAAAAAWjOD8gAAAFliIQ6EYoAAhjxwABA9jgACHlgAAAAAWdCwArd7ARAAAADAEAAAAyjxIngAAAAAWjOD8gAAAFliIICKEYoAAjGxwABCLjgACO5gA==";

    private const string H265AnnexBBase64 =
        "AAAAAUABDAH//wQIAAADAJ+oAAADAAAeugJAAAAAAUIBAQQIAAADAJ+oAAADAAAeoIhFlulvC8BaAgAAAwACAAADADIQAAAAAUQBwHGBEgAAASgBreDHp/65eI5r/+n3AAAAAUABDAH//wQIAAADAJ+oAAADAAAeugJAAAAAAUIBAQQIAAADAJ+oAAADAAAeoIhFlulvC8BaAgAAAwACAAADADIQAAAAAUQBwHGBEgAAASgBrG7ij/8Wl9/ZI9+3zg==";

    private const string AacAdtsBase64 =
        "//FQQBHf/N4CAExhdmM2MC4zMS4xMDIAAnClW2CobVELQn3/p149pm99eZPHs3JIecki5J8IEyVMl0dpEYVMlGFYllqdLU7Vpyq0k5v+qSwHIlWYz8aOo1d2txqm2ybapnBEEU1qa0yUyUyUDAwMDAwMDAwMDAwMDAwMbNgyKWWWKKKKKKKKKKKKKKKLgP/xUEAVn/wBEpTaiV2WS6slUlkun/9j/f8daeert//V/9vv1rjicfp//W/6/fzrrWtf1//qf+f11rrjVie7C0ajXrNN3ksS/+qHD1KU2y7neu+j130beYB9Od5gH0+jzwH0+iXmAAfTn+jzzAA+eJ2z69iNpCQhhERRQxRfKIcS+QlUDGwYlKoGlBgaVTgwMiBgY3Li4MDAwMDUIj0UxU886p59k+zmn+Xy5uD/8VBAD7/8ARaVpojdIuyPVkug7f/2P+v386d/xrX2//z/+V+b6u5x//e9vOtXq9B3ujmsNspna0YxR2X2bDY2RNlvXsGyz+Xq2CJ/LZ8OYsFMQaEmmne+H4sz4n30nz+Rt/Mft17DNozEYmFI4rD++R3LumghbhzNOZ33eP/xUEABn/wBGIG0cA==";

    public static readonly AacCodecConfiguration AacConfiguration =
        new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);

    public static VideoFixture H264
    {
        get
        {
            var nals = SplitAnnexB(Convert.FromBase64String(H264AnnexBBase64));
            return new VideoFixture(
                VideoCodecConfiguration.CreateH264(nals[0], nals[1], 4, 16, 16),
                new[] { nals[2], nals[5] });
        }
    }

    public static VideoFixture H265
    {
        get
        {
            var nals = SplitAnnexB(Convert.FromBase64String(H265AnnexBBase64));
            return new VideoFixture(
                VideoCodecConfiguration.CreateH265(nals[0], nals[1], nals[2], 4, 16, 16),
                new[] { nals[3], nals[7] });
        }
    }

    public static IReadOnlyList<byte[]> AacAccessUnits => ParseAdts(Convert.FromBase64String(AacAdtsBase64));

    public static void WriteMixedFile(string path, VideoFixture video)
    {
        using (var stream = File.Create(path))
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetVideoCodecConfiguration(video.Configuration);
            writer.SetAudioCodecConfiguration(AacConfiguration);
            for (var i = 0; i < video.Frames.Count; i++)
            {
                var timestamp = TimeSpan.FromMilliseconds(i * 40);
                writer.WriteVideoNalUnit(new EncodedVideoNalUnit(video.Frames[i], timestamp, timestamp, TimeSpan.FromMilliseconds(40), true));
            }

            var audioTimestamp = TimeSpan.Zero;
            var audioDuration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / AacConfiguration.SampleRate));
            foreach (var accessUnit in AacAccessUnits.Take(2))
            {
                writer.WriteAudioSample(new EncodedAudioSample(accessUnit, audioTimestamp, audioTimestamp, audioDuration));
                audioTimestamp += audioDuration;
            }

            writer.FinalizeFile();
        }
    }

    public static IReadOnlyList<byte[]> SplitAnnexB(byte[] data)
    {
        var result = new List<byte[]>();
        var payloadStart = -1;
        var codeLength = 0;
        for (var i = 0; i + 2 < data.Length; i++)
        {
            var currentLength = 0;
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) currentLength = 3;
            else if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) currentLength = 4;
            if (currentLength == 0) continue;

            if (payloadStart >= 0)
            {
                var length = i - payloadStart;
                if (length <= 0) throw new InvalidDataException("Fixture contains an empty NAL unit.");
                var nal = new byte[length];
                Buffer.BlockCopy(data, payloadStart, nal, 0, length);
                result.Add(nal);
            }

            payloadStart = i + currentLength;
            codeLength = currentLength;
            i += codeLength - 1;
        }

        if (payloadStart < 0 || payloadStart >= data.Length) throw new InvalidDataException("Fixture contains no NAL units.");
        var finalNal = new byte[data.Length - payloadStart];
        Buffer.BlockCopy(data, payloadStart, finalNal, 0, finalNal.Length);
        result.Add(finalNal);
        return result;
    }

    private static IReadOnlyList<byte[]> ParseAdts(byte[] data)
    {
        var result = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 7 || data[offset] != 0xff || (data[offset + 1] & 0xf6) != 0xf0)
            {
                throw new InvalidDataException("Fixture contains an invalid ADTS header.");
            }

            var headerSize = (data[offset + 1] & 1) == 0 ? 9 : 7;
            var frameLength = ((data[offset + 3] & 3) << 11) | (data[offset + 4] << 3) | (data[offset + 5] >> 5);
            if (frameLength < headerSize || offset > data.Length - frameLength)
            {
                throw new InvalidDataException("Fixture contains an ADTS frame outside its data boundary.");
            }

            var payload = new byte[frameLength - headerSize];
            Buffer.BlockCopy(data, offset + headerSize, payload, 0, payload.Length);
            result.Add(payload);
            offset += frameLength;
        }

        return result;
    }
}

internal sealed class VideoFixture
{
    public VideoFixture(VideoCodecConfiguration configuration, IReadOnlyList<byte[]> frames)
    {
        Configuration = configuration;
        Frames = frames;
    }

    public VideoCodecConfiguration Configuration { get; }
    public IReadOnlyList<byte[]> Frames { get; }
}

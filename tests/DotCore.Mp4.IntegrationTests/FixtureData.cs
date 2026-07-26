using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotCore.Mp4;

namespace DotCore.Mp4.IntegrationTests;

/// <summary>
/// 提供整合測試用的媒體 Fixture 資料。
/// </summary>
internal static class FixtureData
{
    // FFmpeg 6.1.1 lavfi testsrc 16x16@25, 3 frames, GOP=2, no B-frames,
    // libx264 ultrafast/zerolatency with repeated headers and SEI removed.
    private const string H264AnnexBBase64 =
        "AAAAAWdCwAraewEQAAADABAAAAMDKPEiagAAAAFozg/IAAABZYiEOgxgAdAAEGcOUC6tg8te9SsWN+AAs2arfzICYaAt+5aCoVx8XBoo0twCICCvVzlQiI236uxABAEYoiEcOXp48ylu1oBFBSnPhBrlPBwq/4sAQBGOGEUVzUGmHuF+U8QAAIC4AAgDg+4OAIAjAArHYAtVUBQ4Tv6aBGUhp4xD7BwBAEYEAEARgDsuA/EQEGiV/bc5ABvXIAQF9f8GAAIBAACWBGeAC6CFZ18w0aZDVFhCKuZUUcCVuYgPpNkLYBxNtOOAQCJZLySQLbAALB4BQLpHWTWVJc69wA4AofS09YFKMJ7c9MjAAEAxQ1/AcIyR6P/aDcAIh/M8OlrOBNoAAAABQZogJJQAAAABZ0LACtp7ARAAAAMAEAAAAwMo8SJqAAAAAWjOD8gAAAFliIIIgxgALgACBecQXPsRblLdH36ABelv8WNjgFfsqwyCzC3HMP7gkMMi91RY2+dkRAQRilIwZeKSejdrASnIR0ORCgYb+xYIIxxkYyOhzCoehSCAAD4A4u4OCCMAIQAZfQpws7g2RMjiu77BwQRgQEEYDKA2aCEijuBlwP3oGP2/gwACwDSCFiADRDbXMcc8KUQmtYwh5vUoB0tIrAMai6xDbyVpG9d4ADzgUXC1Vq6ha/rQYU2qnsGUxO+1rmAC5v4CC5R0/9o2ACQubgkZtDWg";

    // Same deterministic source encoded by libx265 ultrafast/zerolatency,
    // repeated headers, no B-frames, and prefix/suffix SEI removed.
    private const string H265AnnexBBase64 =
        "AAAAAUABDAH//wFgAAADAJAAAAMAAAMAHroCQAAAAAFCAQEBYAAAAwCQAAADAAADAB6giEWW6W8LwFoCAAADAAIAAAMAMhAAAAABRAHAcYESAAABKAGt4MMEuTHiWXJSyyxW+N6xv4Vq5NEIAF88qQR7s6AK2iwJQofxXQO9JkVjFzGgLp4R1sYfkG3kVpTNd2KjCCxznMfo6w5mRN9PhVa8xlgx/J7hEMaOgHZJJ0B+IoUP8WzcQzW5JAOipWkL8gMFlUuBEY1LN6GVlyrk16YeQpiiPLHACjVDVNbdv3/7c55wYmjYkFHPVPDxKC8ouQa8aExGWgCJuLF2x+ZoRlfUP75/wJyOx9ynyxVnd/vF2pb9g0yzVmXZXX2/w8Psf8R/gLf+H1n0mAAAAAECAdAJeIGs78AAAAABQAEMAf//AWAAAAMAkAAAAwAAAwAeugJAAAAAAUIBAQFgAAADAJAAAAMAAAMAHqCIRZbpbwvAWgIAAAMAAgAAAwAyEAAAAAFEAcBxgRIAAAEqAawI9RSAH9lHHwSnhz2diG3Ost5oqarsNScVT2t9brQQJud9VVWAIrO8xnqbZG5rTcRCmYE4pW2xSkEbxY1AEbk9uoxR/V+tAOX3+HqPMtqT6+ZuCgaEeUF6P1uzt0/JeUtEOF0Uw9//8TYgyC2n2Z4GecXMuGDFJCCeIEHmDFCPsAInv/x+XBK9IEXM1lDJZDQ/q5C/9HdOo0Z2Lq51yJBBwA==";

    private const string AacAdtsBase64 =
        "//FQQBHf/N4CAExhdmM2MC4zMS4xMDIAAnClW2CobVELQn3/p149pm99eZPHs3JIecki5J8IEyVMl0dpEYVMlGFYllqdLU7Vpyq0k5v+qSwHIlWYz8aOo1d2txqm2ybapnBEEU1qa0yUyUyUDAwMDAwMDAwMDAwMDAwMbNgyKWWWKKKKKKKKKKKKKKKLgP/xUEAVn/wBEpTaiV2WS6slUlkun/9j/f8daeert//V/9vv1rjicfp//W/6/fzrrWtf1//qf+f11rrjVie7C0ajXrNN3ksS/+qHD1KU2y7neu+j130beYB9Od5gH0+jzwH0+iXmAAfTn+jzzAA+eJ2z69iNpCQhhERRQxRfKIcS+QlUDGwYlKoGlBgaVTgwMiBgY3Li4MDAwMDUIj0UxU886p59k+zmn+Xy5uD/8VBAD7/8ARaVpojdIuyPVkug7f/2P+v386d/xrX2//z/+V+b6u5x//e9vOtXq9B3ujmsNspna0YxR2X2bDY2RNlvXsGyz+Xq2CJ/LZ8OYsFMQaEmmne+H4sz4n30nz+Rt/Mft17DNozEYmFI4rD++R3LumghbhzNOZ33eP/xUEABn/wBGIG0cA==";

    public static readonly AacCodecConfiguration AacConfiguration =
        AacCodecConfiguration.CreateAacLc(44100, 2);

    public static VideoFixture H264
    {
        get
        {
            var nals = SplitAnnexB(Convert.FromBase64String(H264AnnexBBase64));
            return new VideoFixture(
                VideoCodecConfiguration.CreateH264(
                    nals.First(nal => (nal[0] & 0x1f) == 7),
                    nals.First(nal => (nal[0] & 0x1f) == 8),
                    4,
                    16,
                    16),
                nals.Where(nal => (nal[0] & 0x1f) == 1 || (nal[0] & 0x1f) == 5).ToArray(),
                new[] { true, false, true });
        }
    }

    public static VideoFixture H265
    {
        get
        {
            var nals = SplitAnnexB(Convert.FromBase64String(H265AnnexBBase64));
            return new VideoFixture(
                VideoCodecConfiguration.CreateH265(
                    nals.First(nal => ((nal[0] >> 1) & 0x3f) == 32),
                    nals.First(nal => ((nal[0] >> 1) & 0x3f) == 33),
                    nals.First(nal => ((nal[0] >> 1) & 0x3f) == 34),
                    4,
                    16,
                    16),
                nals.Where(nal => ((nal[0] >> 1) & 0x3f) <= 31).ToArray(),
                new[] { true, false, true });
        }
    }

    public static IReadOnlyList<byte[]> AacAccessUnits => ParseAdts(Convert.FromBase64String(AacAdtsBase64));

    public static void WriteMixedFile(
        string path,
        VideoFixture video,
        Mp4WriteMode mode = Mp4WriteMode.Progressive)
    {
        using (var stream = File.Create(path))
        using (var writer = new Mp4Writer(stream, new Mp4WriterOptions { Mode = mode }))
        {
            WriteMixedCore(writer, video);
            writer.FinalizeFile();
        }
    }

    public static async System.Threading.Tasks.Task WriteMixedFileAsync(
        string path,
        VideoFixture video,
        Mp4WriteMode mode,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var access = mode == Mp4WriteMode.FastStart ? FileAccess.ReadWrite : FileAccess.Write;
        await using (var stream = new FileStream(path, FileMode.Create, access, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
        using (var writer = await Mp4Writer.CreateAsync(stream, new Mp4WriterOptions { Mode = mode }, true, cancellationToken))
        {
            await WriteMixedCoreAsync(writer, video, cancellationToken);
            await writer.FinalizeFileAsync(cancellationToken);
        }
    }

    private static void WriteMixedCore(Mp4Writer writer, VideoFixture video)
    {
        writer.SetVideoCodecConfiguration(video.Configuration);
        writer.SetAudioCodecConfiguration(AacConfiguration);
        var audioDuration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / AacConfiguration.SampleRate));
        var audio = AacAccessUnits.Take(4).ToArray();
        var videoIndex = 0;
        var audioIndex = 0;
        while (videoIndex < video.Frames.Count || audioIndex < audio.Length)
        {
            var videoTimestamp = TimeSpan.FromMilliseconds(videoIndex * 40);
            var audioTimestamp = TimeSpan.FromTicks(audioDuration.Ticks * audioIndex);
            if (videoIndex < video.Frames.Count &&
                (audioIndex >= audio.Length || videoTimestamp <= audioTimestamp))
            {
                writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                    video.Frames[videoIndex],
                    videoTimestamp,
                    videoTimestamp,
                    TimeSpan.FromMilliseconds(40),
                    video.KeyFrames[videoIndex]));
                videoIndex++;
            }
            else
            {
                writer.WriteAudioSample(new EncodedAudioSample(
                    audio[audioIndex],
                    audioTimestamp,
                    audioTimestamp,
                    audioDuration));
                audioIndex++;
            }
        }
    }

    private static async System.Threading.Tasks.Task WriteMixedCoreAsync(
        Mp4Writer writer,
        VideoFixture video,
        System.Threading.CancellationToken cancellationToken)
    {
        writer.SetVideoCodecConfiguration(video.Configuration);
        writer.SetAudioCodecConfiguration(AacConfiguration);
        var audioDuration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / AacConfiguration.SampleRate));
        var audio = AacAccessUnits.Take(4).ToArray();
        var videoIndex = 0;
        var audioIndex = 0;
        while (videoIndex < video.Frames.Count || audioIndex < audio.Length)
        {
            var videoTimestamp = TimeSpan.FromMilliseconds(videoIndex * 40);
            var audioTimestamp = TimeSpan.FromTicks(audioDuration.Ticks * audioIndex);
            if (videoIndex < video.Frames.Count &&
                (audioIndex >= audio.Length || videoTimestamp <= audioTimestamp))
            {
                await writer.WriteVideoNalUnitAsync(new EncodedVideoNalUnit(
                    video.Frames[videoIndex],
                    videoTimestamp,
                    videoTimestamp,
                    TimeSpan.FromMilliseconds(40),
                    video.KeyFrames[videoIndex]), cancellationToken);
                videoIndex++;
            }
            else
            {
                await writer.WriteAudioSampleAsync(new EncodedAudioSample(
                    audio[audioIndex],
                    audioTimestamp,
                    audioTimestamp,
                    audioDuration), cancellationToken);
                audioIndex++;
            }
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
    public VideoFixture(
        VideoCodecConfiguration configuration,
        IReadOnlyList<byte[]> frames,
        IReadOnlyList<bool> keyFrames)
    {
        if (frames.Count != keyFrames.Count) throw new ArgumentException("Frame metadata count mismatch.");
        Configuration = configuration;
        Frames = frames;
        KeyFrames = keyFrames;
    }

    public VideoCodecConfiguration Configuration { get; }
    public IReadOnlyList<byte[]> Frames { get; }
    public IReadOnlyList<bool> KeyFrames { get; }
}

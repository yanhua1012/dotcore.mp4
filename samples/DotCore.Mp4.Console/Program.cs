using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;

internal static class Program
{
    private const string H264AnnexBBase64 =
        "AAAAAWdCwAraewEQAAADABAAAAMDKPEiagAAAAFozg/IAAABZYiEOgxgAdAAEGcOUC6tg8te9SsWN+AAs2arfzICYaAt+5aCoVx8XBoo0twCICCvVzlQiI236uxABAEYoiEcOXp48ylu1oBFBSnPhBrlPBwq/4sAQBGOGEUVzUGmHuF+U8QAAIC4AAgDg+4OAIAjAArHYAtVUBQ4Tv6aBGUhp4xD7BwBAEYEAEARgDsuA/EQEGiV/bc5ABvXIAQF9f8GAAIBAACWBGeAC6CFZ18w0aZDVFhCKuZUUcCVuYgPpNkLYBxNtOOAQCJZLySQLbAALB4BQLpHWTWVJc69wA4AofS09YFKMJ7c9MjAAEAxQ1/AcIyR6P/aDcAIh/M8OlrOBNoAAAABQZogJJQAAAABZ0LACtp7ARAAAAMAEAAAAwMo8SJqAAAAAWjOD8gAAAFliIIIgxgALgACBecQXPsRblLdH36ABelv8WNjgFfsqwyCzC3HMP7gkMMi91RY2+dkRAQRilIwZeKSejdrASnIR0ORCgYb+xYIIxxkYyOhzCoehSCAAD4A4u4OCCMAIQAZfQpws7g2RMjiu77BwQRgQEEYDKA2aCEijuBlwP3oGP2/gwACwDSCFiADRDbXMcc8KUQmtYwh5vUoB0tIrAMai6xDbyVpG9d4ADzgUXC1Vq6ha/rQYU2qnsGUxO+1rmAC5v4CC5R0/9o2ACQubgkZtDWg";

    private const string H265AnnexBBase64 =
        "AAAAAUABDAH//wFgAAADAJAAAAMAAAMAHroCQAAAAAFCAQEBYAAAAwCQAAADAAADAB6giEWW6W8LwFoCAAADAAIAAAMAMhAAAAABRAHAcYESAAABKAGt4MMEuTHiWXJSyyxW+N6xv4Vq5NEIAF88qQR7s6AK2iwJQofxXQO9JkVjFzGgLp4R1sYfkG3kVpTNd2KjCCxznMfo6w5mRN9PhVa8xlgx/J7hEMaOgHZJJ0B+IoUP8WzcQzW5JAOipWkL8gMFlUuBEY1LN6GVlyrk16YeQpiiPLHACjVDVNbdv3/7c55wYmjYkFHPVPDxKC8ouQa8aExGWgCJuLF2x+ZoRlfUP75/wJyOx9ynyxVnd/vF2pb9g0yzVmXZXX2/w8Psf8R/gLf+H1n0mAAAAAECAdAJeIGs78AAAAABQAEMAf//AWAAAAMAkAAAAwAAAwAeugJAAAAAAUIBAQFgAAADAJAAAAMAAAMAHqCIRZbpbwvAWgIAAAMAAgAAAwAyEAAAAAFEAcBxgRIAAAEqAawI9RSAH9lHHwSnhz2diG3Ost5oqarsNScVT2t9brQQJud9VVWAIrO8xnqbZG5rTcRCmYE4pW2xSkEbxY1AEbk9uoxR/V+tAOX3+HqPMtqT6+ZuCgaEeUF6P1uzt0/JeUtEOF0Uw9//8TYgyC2n2Z4GecXMuGDFJCCeIEHmDFCPsAInv/x+XBK9IEXM1lDJZDQ/q5C/9HdOo0Z2Lq51yJBBwA==";

    private static readonly byte[] AacAccessUnit = Convert.FromBase64String(
        "3gIATGF2YzYwLjMxLjEwMgACcKVbYKhtUQtCff+nXj2mb315k8ezckh5ySLknwgTJUyXR2kRhUyUYViWWp0tTtWnKrSTm/6pLAciVZjPxo6jV3a3GqbbJtqmcEQRTWprTJTJTJQMDAwMDAwMDAwMDAwMDAxs2DIpZZYooooooooooooooouA");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 4 ||
            !TryParseMode(args.Length > 1 ? args[1] : null, out var mode, out var modeName) ||
            !TryParseCodec(args.Length > 2 ? args[2] : null, out var codec, out var codecName) ||
            !TryParseIoMode(args.Length > 3 ? args[3] : null, out var useAsync, out var ioName))
        {
            Console.Error.WriteLine(
                "Usage: DotCore.Mp4.Console <output-path> [progressive|faststart|fragmented] [h264|h265] [sync|async]");
            return 2;
        }

        var outputPath = Path.GetFullPath(args.Length == 0 ? "dotcore-demo.mp4" : args[0]);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var fixture = CreateVideoFixture(codec);
        var audioConfiguration = AacCodecConfiguration.CreateAacLc(44100, 2);
        var sampleDuration = TimeSpan.FromMilliseconds(40);
        var audioDuration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / audioConfiguration.SampleRate));

        if (useAsync)
        {
            await WriteAsync(outputPath, mode, fixture, audioConfiguration, sampleDuration, audioDuration);
        }
        else
        {
            WriteSync(outputPath, mode, fixture, audioConfiguration, sampleDuration, audioDuration);
        }

        Console.WriteLine("Mode: " + modeName);
        Console.WriteLine("Codec: " + codecName);
        Console.WriteLine("I/O: " + ioName);
        Console.WriteLine("MP4: " + outputPath);

        using (var stream = File.OpenRead(outputPath))
        using (var reader = useAsync
            ? await Mp4Reader.CreateAsync(stream)
            : new Mp4Reader(stream))
        {
            PrintReader(reader);
        }

        return 0;
    }

    private static void WriteSync(
        string outputPath,
        Mp4WriteMode mode,
        VideoFixture fixture,
        AacCodecConfiguration audioConfiguration,
        TimeSpan sampleDuration,
        TimeSpan audioDuration)
    {
        using var stream = File.Create(outputPath);
        using var writer = new Mp4Writer(stream, new Mp4WriterOptions { Mode = mode });
        writer.SetVideoCodecConfiguration(fixture.Configuration);
        writer.SetAudioCodecConfiguration(audioConfiguration);
        SubmitSamplesSync(writer, fixture, sampleDuration, audioDuration);
        writer.FinalizeFile();
    }

    private static async Task WriteAsync(
        string outputPath,
        Mp4WriteMode mode,
        VideoFixture fixture,
        AacCodecConfiguration audioConfiguration,
        TimeSpan sampleDuration,
        TimeSpan audioDuration)
    {
        var access = mode == Mp4WriteMode.FastStart ? FileAccess.ReadWrite : FileAccess.Write;
        using var stream = new FileStream(outputPath, FileMode.Create, access, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        using var writer = await Mp4Writer.CreateAsync(stream, new Mp4WriterOptions { Mode = mode });
        writer.SetVideoCodecConfiguration(fixture.Configuration);
        writer.SetAudioCodecConfiguration(audioConfiguration);
        await SubmitSamplesAsync(writer, fixture, sampleDuration, audioDuration);
        await writer.FinalizeFileAsync();
    }

    private static void SubmitSamplesSync(
        Mp4Writer writer,
        VideoFixture fixture,
        TimeSpan sampleDuration,
        TimeSpan audioDuration)
    {
        var videoIndex = 0;
        var audioIndex = 0;
        while (videoIndex < fixture.Frames.Count || audioIndex < 4)
        {
            var videoTimestamp = TimeSpan.FromTicks(sampleDuration.Ticks * videoIndex);
            var audioTimestamp = TimeSpan.FromTicks(audioDuration.Ticks * audioIndex);
            if (videoIndex < fixture.Frames.Count && (audioIndex >= 4 || videoTimestamp <= audioTimestamp))
            {
                writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                    fixture.Frames[videoIndex],
                    videoTimestamp,
                    videoTimestamp,
                    sampleDuration,
                    fixture.KeyFrames[videoIndex]));
                videoIndex++;
            }
            else
            {
                writer.WriteAudioSample(new EncodedAudioSample(
                    AacAccessUnit,
                    audioTimestamp,
                    audioTimestamp,
                    audioDuration));
                audioIndex++;
            }
        }
    }

    private static async Task SubmitSamplesAsync(
        Mp4Writer writer,
        VideoFixture fixture,
        TimeSpan sampleDuration,
        TimeSpan audioDuration)
    {
        var videoIndex = 0;
        var audioIndex = 0;
        while (videoIndex < fixture.Frames.Count || audioIndex < 4)
        {
            var videoTimestamp = TimeSpan.FromTicks(sampleDuration.Ticks * videoIndex);
            var audioTimestamp = TimeSpan.FromTicks(audioDuration.Ticks * audioIndex);
            if (videoIndex < fixture.Frames.Count && (audioIndex >= 4 || videoTimestamp <= audioTimestamp))
            {
                await writer.WriteVideoNalUnitAsync(new EncodedVideoNalUnit(
                    fixture.Frames[videoIndex],
                    videoTimestamp,
                    videoTimestamp,
                    sampleDuration,
                    fixture.KeyFrames[videoIndex]));
                videoIndex++;
            }
            else
            {
                await writer.WriteAudioSampleAsync(new EncodedAudioSample(
                    AacAccessUnit,
                    audioTimestamp,
                    audioTimestamp,
                    audioDuration));
                audioIndex++;
            }
        }
    }

    private static void PrintReader(Mp4Reader reader)
    {
        var parsedVideo = reader.VideoConfiguration ??
                          throw new Mp4FormatException("The generated MP4 does not contain a parsed video configuration.");
        var parsedAudio = reader.AudioConfiguration ??
                          throw new Mp4FormatException("The generated MP4 does not contain a parsed AAC configuration.");
        if (parsedVideo.Codec == VideoCodec.H265)
        {
            Console.WriteLine("Parsed H.265 VPS: " + Hex(parsedVideo.Vps!));
            Console.WriteLine("Parsed H.265 SPS: " + Hex(parsedVideo.Sps));
            Console.WriteLine("Parsed H.265 PPS: " + Hex(parsedVideo.Pps));
        }
        else
        {
            Console.WriteLine("Parsed H.264 SPS: " + Hex(parsedVideo.Sps));
            Console.WriteLine("Parsed H.264 PPS: " + Hex(parsedVideo.Pps));
        }
        Console.WriteLine("Parsed AAC: objectType=" + parsedAudio.AudioObjectType +
                          " sampleRate=" + parsedAudio.SampleRate +
                          " channels=" + parsedAudio.ChannelConfiguration +
                          " ASC=" + Hex(parsedAudio.AudioSpecificConfig));
        reader.VideoNalUnitRead += (_, eventArgs) => Console.WriteLine(
            "Video NAL: size=" + eventArgs.Data.Length +
            " pts=" + eventArgs.PresentationTimestamp +
            " dts=" + eventArgs.DecodeTimestamp +
            " duration=" + eventArgs.Duration +
            " key=" + eventArgs.IsKeyFrame);
        reader.AacSampleRead += (_, eventArgs) => Console.WriteLine(
            "AAC sample: size=" + eventArgs.Data.Length +
            " pts=" + eventArgs.PresentationTimestamp +
            " dts=" + eventArgs.DecodeTimestamp +
            " duration=" + eventArgs.Duration);
        reader.Read();
    }

    private static bool TryParseIoMode(string? value, out bool useAsync, out string ioName)
    {
        ioName = string.IsNullOrEmpty(value) ? "sync" : value.ToLowerInvariant();
        switch (ioName)
        {
            case "sync":
                useAsync = false;
                return true;
            case "async":
                useAsync = true;
                return true;
            default:
                useAsync = false;
                return false;
        }
    }

    private static bool TryParseMode(
        string? value,
        out Mp4WriteMode mode,
        out string modeName)
    {
        modeName = string.IsNullOrEmpty(value) ? "progressive" : value.ToLowerInvariant();
        switch (modeName)
        {
            case "progressive":
                mode = Mp4WriteMode.Progressive;
                return true;
            case "faststart":
                mode = Mp4WriteMode.FastStart;
                return true;
            case "fragmented":
                mode = Mp4WriteMode.Fragmented;
                return true;
            default:
                mode = Mp4WriteMode.Progressive;
                return false;
        }
    }

    private static bool TryParseCodec(string? value, out VideoCodec codec, out string codecName)
    {
        codecName = string.IsNullOrEmpty(value) ? "h264" : value.ToLowerInvariant();
        switch (codecName)
        {
            case "h264":
                codec = VideoCodec.H264;
                return true;
            case "h265":
                codec = VideoCodec.H265;
                return true;
            default:
                codec = VideoCodec.H264;
                return false;
        }
    }

    private static VideoFixture CreateVideoFixture(VideoCodec codec)
    {
        var nals = SplitAnnexB(Convert.FromBase64String(
            codec == VideoCodec.H265 ? H265AnnexBBase64 : H264AnnexBBase64));
        if (codec == VideoCodec.H265)
        {
            var frames = nals.Where(nal => H265NalType(nal) <= 31).ToArray();
            return new VideoFixture(
                VideoCodecConfiguration.CreateH265(
                    nals.First(nal => H265NalType(nal) == 32),
                    nals.First(nal => H265NalType(nal) == 33),
                    nals.First(nal => H265NalType(nal) == 34),
                    4,
                    16,
                    16),
                frames,
                frames.Select(nal => H265NalType(nal) >= 16 && H265NalType(nal) <= 23).ToArray());
        }

        var h264Frames = nals.Where(nal => (nal[0] & 0x1f) == 1 || (nal[0] & 0x1f) == 5).ToArray();
        return new VideoFixture(
            VideoCodecConfiguration.CreateH264(
                nals.First(nal => (nal[0] & 0x1f) == 7),
                nals.First(nal => (nal[0] & 0x1f) == 8),
                4,
                16,
                16),
            h264Frames,
            h264Frames.Select(nal => (nal[0] & 0x1f) == 5).ToArray());
    }

    private static int H265NalType(byte[] nal)
    {
        if (nal.Length < 2) throw new InvalidDataException("The Console H.265 fixture contains an incomplete NAL unit.");
        return (nal[0] >> 1) & 0x3f;
    }

    private static IReadOnlyList<byte[]> SplitAnnexB(byte[] data)
    {
        var result = new List<byte[]>();
        var payloadStart = -1;
        for (var index = 0; index + 2 < data.Length; index++)
        {
            var codeLength = 0;
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1) codeLength = 3;
            else if (index + 3 < data.Length &&
                     data[index] == 0 &&
                     data[index + 1] == 0 &&
                     data[index + 2] == 0 &&
                     data[index + 3] == 1) codeLength = 4;
            if (codeLength == 0) continue;
            if (payloadStart >= 0)
            {
                var nal = new byte[index - payloadStart];
                Buffer.BlockCopy(data, payloadStart, nal, 0, nal.Length);
                result.Add(nal);
            }

            payloadStart = index + codeLength;
            index += codeLength - 1;
        }

        if (payloadStart < 0 || payloadStart >= data.Length)
        {
            throw new InvalidDataException("The Console video fixture contains no NAL units.");
        }

        var finalNal = new byte[data.Length - payloadStart];
        Buffer.BlockCopy(data, payloadStart, finalNal, 0, finalNal.Length);
        result.Add(finalNal);
        return result;
    }

    private static string Hex(byte[] value)
    {
        return BitConverter.ToString(value).Replace("-", string.Empty);
    }

    private sealed class VideoFixture
    {
        public VideoFixture(
            VideoCodecConfiguration configuration,
            IReadOnlyList<byte[]> frames,
            IReadOnlyList<bool> keyFrames)
        {
            if (frames.Count != keyFrames.Count) throw new ArgumentException("Video fixture metadata count mismatch.");
            Configuration = configuration;
            Frames = frames;
            KeyFrames = keyFrames;
        }

        public VideoCodecConfiguration Configuration { get; }
        public IReadOnlyList<byte[]> Frames { get; }
        public IReadOnlyList<bool> KeyFrames { get; }
    }
}

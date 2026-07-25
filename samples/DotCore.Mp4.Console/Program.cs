using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotCore.Mp4;

internal static class Program
{
    private const string H264AnnexBBase64 =
        "AAAAAWdCwAraewEQAAADABAAAAMDKPEiagAAAAFozg/IAAABZYiEOgxgAdAAEGcOUC6tg8te9SsWN+AAs2arfzICYaAt+5aCoVx8XBoo0twCICCvVzlQiI236uxABAEYoiEcOXp48ylu1oBFBSnPhBrlPBwq/4sAQBGOGEUVzUGmHuF+U8QAAIC4AAgDg+4OAIAjAArHYAtVUBQ4Tv6aBGUhp4xD7BwBAEYEAEARgDsuA/EQEGiV/bc5ABvXIAQF9f8GAAIBAACWBGeAC6CFZ18w0aZDVFhCKuZUUcCVuYgPpNkLYBxNtOOAQCJZLySQLbAALB4BQLpHWTWVJc69wA4AofS09YFKMJ7c9MjAAEAxQ1/AcIyR6P/aDcAIh/M8OlrOBNoAAAABQZogJJQAAAABZ0LACtp7ARAAAAMAEAAAAwMo8SJqAAAAAWjOD8gAAAFliIIIgxgALgACBecQXPsRblLdH36ABelv8WNjgFfsqwyCzC3HMP7gkMMi91RY2+dkRAQRilIwZeKSejdrASnIR0ORCgYb+xYIIxxkYyOhzCoehSCAAD4A4u4OCCMAIQAZfQpws7g2RMjiu77BwQRgQEEYDKA2aCEijuBlwP3oGP2/gwACwDSCFiADRDbXMcc8KUQmtYwh5vUoB0tIrAMai6xDbyVpG9d4ADzgUXC1Vq6ha/rQYU2qnsGUxO+1rmAC5v4CC5R0/9o2ACQubgkZtDWg";

    private static readonly byte[] AacAccessUnit = Convert.FromBase64String(
        "3gIATGF2YzYwLjMxLjEwMgACcKVbYKhtUQtCff+nXj2mb315k8ezckh5ySLknwgTJUyXR2kRhUyUYViWWp0tTtWnKrSTm/6pLAciVZjPxo6jV3a3GqbbJtqmcEQRTWprTJTJTJQMDAwMDAwMDAwMDAwMDAxs2DIpZZYooooooooooooooouA");

    private static int Main(string[] args)
    {
        if (args.Length > 2 || !TryParseMode(args.Length > 1 ? args[1] : null, out var mode, out var modeName))
        {
            Console.Error.WriteLine("Usage: DotCore.Mp4.Console <output-path> [progressive|faststart|fragmented]");
            return 2;
        }

        var outputPath = Path.GetFullPath(args.Length == 0 ? "dotcore-demo.mp4" : args[0]);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var nals = SplitAnnexB(Convert.FromBase64String(H264AnnexBBase64));
        var sps = nals.First(nal => (nal[0] & 0x1f) == 7);
        var pps = nals.First(nal => (nal[0] & 0x1f) == 8);
        var videoFrames = nals.Where(nal => (nal[0] & 0x1f) == 1 || (nal[0] & 0x1f) == 5).ToArray();
        var keyFrames = videoFrames.Select(nal => (nal[0] & 0x1f) == 5).ToArray();
        var videoConfiguration = VideoCodecConfiguration.CreateH264(sps, pps, 4, 16, 16);
        var audioConfiguration = new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);
        var sampleDuration = TimeSpan.FromMilliseconds(40);
        var audioDuration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / audioConfiguration.SampleRate));

        using (var stream = File.Create(outputPath))
        using (var writer = new Mp4Writer(stream, new Mp4WriterOptions { Mode = mode }))
        {
            writer.SetVideoCodecConfiguration(videoConfiguration);
            writer.SetAudioCodecConfiguration(audioConfiguration);
            var videoIndex = 0;
            var audioIndex = 0;
            while (videoIndex < videoFrames.Length || audioIndex < 4)
            {
                var videoTimestamp = TimeSpan.FromTicks(sampleDuration.Ticks * videoIndex);
                var audioTimestamp = TimeSpan.FromTicks(audioDuration.Ticks * audioIndex);
                if (videoIndex < videoFrames.Length && (audioIndex >= 4 || videoTimestamp <= audioTimestamp))
                {
                    writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                        videoFrames[videoIndex],
                        videoTimestamp,
                        videoTimestamp,
                        sampleDuration,
                        keyFrames[videoIndex]));
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
            writer.FinalizeFile();
        }

        Console.WriteLine("Mode: " + modeName);
        Console.WriteLine("MP4: " + outputPath);

        using (var stream = File.OpenRead(outputPath))
        using (var reader = new Mp4Reader(stream))
        {
            var parsedVideo = reader.VideoConfiguration ??
                              throw new Mp4FormatException("The generated MP4 does not contain a parsed video configuration.");
            var parsedAudio = reader.AudioConfiguration ??
                              throw new Mp4FormatException("The generated MP4 does not contain a parsed AAC configuration.");
            Console.WriteLine("Parsed H.264 SPS: " + Hex(parsedVideo.Sps));
            Console.WriteLine("Parsed H.264 PPS: " + Hex(parsedVideo.Pps));
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

        return 0;
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
            throw new InvalidDataException("The Console H.264 fixture contains no NAL units.");
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
}

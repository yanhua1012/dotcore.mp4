using System;
using System.IO;
using DotCore.Mp4;

internal static class Program
{
    private static readonly byte[] Sps =
    {
        0x67, 0x42, 0xc0, 0x0a, 0xdd, 0xec, 0x04, 0x40, 0x00, 0x00, 0x03, 0x00,
        0x40, 0x00, 0x00, 0x0c, 0xa3, 0xc4, 0x89, 0xe0
    };

    private static readonly byte[] Pps = { 0x68, 0xce, 0x0f, 0xc8 };

    private static readonly byte[][] VideoFrames =
    {
        new byte[] { 0x65, 0x88, 0x84, 0x3a, 0x11, 0x8a, 0x00, 0x02, 0x18, 0xf1, 0xc0, 0x00, 0x40, 0xf6, 0x38, 0x00, 0x08, 0x79, 0x60 },
        new byte[] { 0x65, 0x88, 0x82, 0x02, 0x28, 0x46, 0x28, 0x00, 0x08, 0xc6, 0xc7, 0x00, 0x01, 0x08, 0xb8, 0xe0, 0x00, 0x23, 0xb9, 0x80 }
    };

    private static readonly byte[] AacAccessUnit = Convert.FromBase64String(
        "3gIATGF2YzYwLjMxLjEwMgACcKVbYKhtUQtCff+nXj2mb315k8ezckh5ySLknwgTJUyXR2kRhUyUYViWWp0tTtWnKrSTm/6pLAciVZjPxo6jV3a3GqbbJtqmcEQRTWprTJTJTJQMDAwMDAwMDAwMDAwMDAxs2DIpZZYooooooooooooooouA");

    private static int Main(string[] args)
    {
        var outputPath = Path.GetFullPath(args.Length == 0 ? "dotcore-demo.mp4" : args[0]);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var videoConfiguration = VideoCodecConfiguration.CreateH264(Sps, Pps, 4, 16, 16);
        var audioConfiguration = new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);
        var sampleDuration = TimeSpan.FromMilliseconds(40);
        var audioDuration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / audioConfiguration.SampleRate));

        using (var stream = File.Create(outputPath))
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetVideoCodecConfiguration(videoConfiguration);
            writer.SetAudioCodecConfiguration(audioConfiguration);
            for (var i = 0; i < VideoFrames.Length; i++)
            {
                var timestamp = TimeSpan.FromTicks(sampleDuration.Ticks * i);
                writer.WriteVideoNalUnit(new EncodedVideoNalUnit(VideoFrames[i], timestamp, timestamp, sampleDuration, true));
            }

            writer.WriteAudioSample(new EncodedAudioSample(AacAccessUnit, TimeSpan.Zero, TimeSpan.Zero, audioDuration));
            writer.WriteAudioSample(new EncodedAudioSample(AacAccessUnit, audioDuration, audioDuration, audioDuration));
            writer.FinalizeFile();
        }

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

    private static string Hex(byte[] value)
    {
        return BitConverter.ToString(value).Replace("-", string.Empty);
    }
}

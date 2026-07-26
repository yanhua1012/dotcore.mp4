using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

internal static class FixedFixtureMatrix
{
    private const int LargeNalBytes = 128 * 1024;
    private const int LargeAacBytes = 128 * 1024;
    private const int TinyNalCount = 128;

    public static IReadOnlyList<BenchmarkScenario> Scenarios { get; } = CreateScenarios();

    public static VideoCodecConfiguration VideoConfiguration(VideoCodec codec)
    {
        return codec == VideoCodec.H264
            ? VideoCodecConfiguration.CreateH264(
                new byte[] { 0x67, 0x42, 0x00, 0x1e },
                new byte[] { 0x68, 0xce, 0x06, 0xe2 },
                4,
                16,
                16)
            : VideoCodecConfiguration.CreateH265(
                new byte[] { 0x40, 0x01 },
                new byte[] { 0x42, 0x01 },
                new byte[] { 0x44, 0x01 },
                4,
                16,
                16);
    }

    public static AacCodecConfiguration AacConfiguration()
    {
        return new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);
    }

    public static byte[] VideoNal(VideoCodec codec, int size, bool keyFrame, int seed)
    {
        if (size < 2) throw new ArgumentOutOfRangeException(nameof(size));
        var bytes = DeterministicBytes(size, seed);
        if (codec == VideoCodec.H264)
        {
            bytes[0] = keyFrame ? (byte)0x65 : (byte)0x41;
        }
        else
        {
            bytes[0] = keyFrame ? (byte)0x26 : (byte)0x02;
            bytes[1] = 0x01;
        }

        return bytes;
    }

    public static byte[] AacAccessUnit(int size, int seed) => DeterministicBytes(size, seed);

    public static byte[] AnnexB(params byte[][] nals)
    {
        var length = nals.Sum(nal => checked(4 + nal.Length));
        var result = new byte[length];
        var offset = 0;
        foreach (var nal in nals)
        {
            result[offset + 3] = 1;
            offset += 4;
            Buffer.BlockCopy(nal, 0, result, offset, nal.Length);
            offset += nal.Length;
        }

        return result;
    }

    public static byte[] CreateReaderFile(BenchmarkScenario scenario)
    {
        using var output = new MemoryStream(EstimateOutputCapacity(scenario));
        using (var writer = new Mp4Writer(
                   output,
                   new Mp4WriterOptions
                   {
                       Mode = scenario.Layout ?? Mp4WriteMode.Progressive,
                       MaximumFragmentBufferBytes = 32 * 1024 * 1024
                   }))
        {
            if (scenario.Payload == "aac")
            {
                writer.SetAudioCodecConfiguration(AacConfiguration());
                for (var index = 0; index < scenario.SampleCount; index++)
                {
                    var timestamp = TimeSpan.FromMilliseconds(index * 20);
                    writer.WriteAudioSample(new EncodedAudioSample(
                        AacAccessUnit(LargeAacBytes, 1000 + index),
                        timestamp,
                        timestamp,
                        TimeSpan.FromMilliseconds(20)));
                }
            }
            else
            {
                var codec = scenario.Codec ?? VideoCodec.H264;
                writer.SetVideoCodecConfiguration(VideoConfiguration(codec));
                for (var index = 0; index < scenario.SampleCount; index++)
                {
                    var timestamp = TimeSpan.FromMilliseconds(index * 40);
                    var keyFrame = index == 0 || index % Math.Max(1, scenario.GopLength) == 0;
                    if (scenario.Shape == NalShape.Multi)
                    {
                        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                            VideoNal(codec, LargeNalBytes / 2, keyFrame, index * 2),
                            timestamp,
                            timestamp,
                            TimeSpan.FromMilliseconds(40),
                            keyFrame));
                        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                            VideoNal(codec, LargeNalBytes / 2, keyFrame, index * 2 + 1),
                            timestamp,
                            timestamp,
                            TimeSpan.FromMilliseconds(40),
                            keyFrame));
                    }
                    else
                    {
                        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                            VideoNal(codec, LargeNalBytes, keyFrame, index),
                            timestamp,
                            timestamp,
                            TimeSpan.FromMilliseconds(40),
                            keyFrame));
                    }
                }
            }

            writer.FinalizeFile();
        }

        return output.ToArray();
    }

    public static IReadOnlyList<EncodedVideoNalUnit> CreateWriterVideoSamples(BenchmarkScenario scenario)
    {
        var codec = scenario.Codec ?? VideoCodec.H264;
        var result = new List<EncodedVideoNalUnit>();
        var sampleCount = Math.Max(
            1,
            scenario.Operation == BenchmarkOperation.FragmentFlush
                ? scenario.SampleCount + 1
                : scenario.SampleCount);
        for (var index = 0; index < sampleCount; index++)
        {
            var timestamp = TimeSpan.FromMilliseconds(index * 40);
            var keyFrame = index == 0 || index % Math.Max(1, scenario.GopLength) == 0;
            if (scenario.Shape == NalShape.Tiny)
            {
                var tinyNals = Enumerable.Range(0, TinyNalCount)
                    .Select(nalIndex => VideoNal(codec, 8, keyFrame, index * TinyNalCount + nalIndex))
                    .ToArray();
                result.Add(CreateVideoSample(AnnexB(tinyNals), timestamp, keyFrame));
            }
            else if (scenario.Shape == NalShape.Multi && scenario.Input == VideoInputKind.Raw)
            {
                result.Add(CreateVideoSample(
                    VideoNal(codec, LargeNalBytes / 2, keyFrame, index * 2),
                    timestamp,
                    keyFrame));
                result.Add(CreateVideoSample(
                    VideoNal(codec, LargeNalBytes / 2, keyFrame, index * 2 + 1),
                    timestamp,
                    keyFrame));
            }
            else
            {
                var first = VideoNal(codec, LargeNalBytes / (scenario.Shape == NalShape.Multi ? 2 : 1), keyFrame, index * 2);
                var data = scenario.Shape == NalShape.Multi
                    ? AnnexB(first, VideoNal(codec, LargeNalBytes / 2, keyFrame, index * 2 + 1))
                    : scenario.Input == VideoInputKind.AnnexB ? AnnexB(first) : first;
                result.Add(CreateVideoSample(data, timestamp, keyFrame));
            }
        }

        return result;
    }

    public static IReadOnlyList<EncodedAudioSample> CreateWriterAudioSamples(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var timestamp = TimeSpan.FromMilliseconds(index * 20);
                return new EncodedAudioSample(
                    AacAccessUnit(1024, 2000 + index),
                    timestamp,
                    timestamp,
                    TimeSpan.FromMilliseconds(20));
            })
            .ToArray();
    }

    public static int EstimateOutputCapacity(BenchmarkScenario scenario)
    {
        return checked(Math.Max(1 * 1024 * 1024, scenario.LogicalPayloadBytes + 512 * 1024));
    }

    private static EncodedVideoNalUnit CreateVideoSample(byte[] data, TimeSpan timestamp, bool keyFrame)
    {
        return new EncodedVideoNalUnit(
            data,
            timestamp,
            timestamp,
            TimeSpan.FromMilliseconds(40),
            keyFrame);
    }

    private static byte[] DeterministicBytes(int size, int seed)
    {
        var result = new byte[size];
        var value = unchecked((uint)(seed + 1) * 2654435761U);
        for (var index = 0; index < result.Length; index++)
        {
            value = unchecked(value * 1664525U + 1013904223U);
            result[index] = (byte)(value >> 24);
        }

        return result;
    }

    private static IReadOnlyList<BenchmarkScenario> CreateScenarios()
    {
        var result = new List<BenchmarkScenario>();
        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
        {
            var codecName = codec == VideoCodec.H264 ? "h264" : "h265";
            result.Add(Reader("reader.constructor." + codecName + ".progressive.large-video", BenchmarkOperation.ReaderConstructor, codec, "video", NalShape.Single, false, false, false));
            result.Add(Reader("reader.delivery.no-event." + codecName + ".large-video.single-nal", BenchmarkOperation.ReaderDelivery, codec, "video", NalShape.Single, false, false, true));
            result.Add(Reader("reader.delivery.no-event." + codecName + ".large-video.multi-nal", BenchmarkOperation.ReaderDelivery, codec, "video", NalShape.Multi, false, false, true));
            result.Add(Reader("reader.delivery.events." + codecName + ".large-video", BenchmarkOperation.ReaderDelivery, codec, "video", NalShape.Single, true, false, false));
            result.Add(Reader("reader.delivery.data-access." + codecName + ".large-video", BenchmarkOperation.ReaderDelivery, codec, "video", NalShape.Single, false, true, false));

            foreach (var layout in new[] { Mp4WriteMode.Progressive, Mp4WriteMode.FastStart })
            {
                foreach (var input in new[] { VideoInputKind.Raw, VideoInputKind.AnnexB })
                {
                    foreach (var shape in new[] { NalShape.Single, NalShape.Multi })
                    {
                        var id = "writer.ingestion." + layout.ToString().ToLowerInvariant() + "." +
                                 codecName + "." + input.ToString().ToLowerInvariant() + "." +
                                 shape.ToString().ToLowerInvariant();
                        result.Add(Writer(id, BenchmarkOperation.WriterIngestion, codec, layout, input, shape, 8, 8, true, true));
                    }
                }
            }

            result.Add(Writer("writer.ingestion.progressive." + codecName + ".annexb.tiny-nals", BenchmarkOperation.WriterIngestion, codec, Mp4WriteMode.Progressive, VideoInputKind.AnnexB, NalShape.Tiny, 1, 1, false, false));
            result.Add(Writer("writer.fragment-flush." + codecName + ".short-gop", BenchmarkOperation.FragmentFlush, codec, Mp4WriteMode.Fragmented, VideoInputKind.AnnexB, NalShape.Multi, 8, 8, true, true));
            result.Add(Writer("writer.fragment-flush." + codecName + ".long-gop", BenchmarkOperation.FragmentFlush, codec, Mp4WriteMode.Fragmented, VideoInputKind.AnnexB, NalShape.Multi, 30, 30, true, true));
            // Finalization moves the completed media payload. Keep this fixture large
            // enough that scheduler jitter cannot dominate the measured operation.
            result.Add(Writer("writer.finalize.faststart." + codecName + ".annexb", BenchmarkOperation.FastStartFinalization, codec, Mp4WriteMode.FastStart, VideoInputKind.AnnexB, NalShape.Multi, 64, 8, true, false));
        }

        result.Add(Reader("reader.delivery.no-event.aac.large", BenchmarkOperation.ReaderDelivery, null, "aac", NalShape.None, false, false, true));
        result.Add(Reader("reader.delivery.events.aac.large", BenchmarkOperation.ReaderDelivery, null, "aac", NalShape.None, true, false, false));
        result.Add(Reader("reader.delivery.data-access.aac.large", BenchmarkOperation.ReaderDelivery, null, "aac", NalShape.None, false, true, false));

        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
        {
            var codecName = codec == VideoCodec.H264 ? "h264" : "h265";
            result.Add(AsyncReader("async.reader.snapshot." + codecName + ".large-video", codec, "video", NalShape.Single));
            result.Add(AsyncWriter("async.writer.ingestion.progressive." + codecName + ".annexb.multi", codec, Mp4WriteMode.Progressive, VideoInputKind.AnnexB, NalShape.Multi, 8, 8));
            result.Add(AsyncWriter("async.writer.fragment-flush." + codecName + ".short-gop", codec, Mp4WriteMode.Fragmented, VideoInputKind.AnnexB, NalShape.Multi, 8, 8));
            result.Add(AsyncWriter("async.writer.finalize.faststart." + codecName + ".annexb", codec, Mp4WriteMode.FastStart, VideoInputKind.AnnexB, NalShape.Multi, 64, 8));
            result.Add(AsyncFile("async.reader.snapshot.file." + codecName + ".large-video", codec, BenchmarkOperation.AsyncReaderSnapshot, Mp4WriteMode.Progressive));
            result.Add(AsyncFile("async.writer.ingestion.file.progressive." + codecName + ".annexb", codec, BenchmarkOperation.AsyncWriterIngestion, Mp4WriteMode.Progressive));
            result.Add(AsyncFile("async.writer.finalize.file.faststart." + codecName + ".annexb", codec, BenchmarkOperation.AsyncFastStartFinalization, Mp4WriteMode.FastStart));
            result.Add(AsyncFile("async.writer.fragment-flush.file.fragmented." + codecName + ".annexb", codec, BenchmarkOperation.AsyncFragmentFlush, Mp4WriteMode.Fragmented));
        }

        foreach (var concurrency in new[] { 1, 32, 128 })
        {
            result.Add(AsyncConcurrency("async.concurrency.reader." + concurrency + ".h264", VideoCodec.H264, BenchmarkOperation.AsyncReaderSnapshot, concurrency));
            result.Add(AsyncConcurrency("async.concurrency.writer." + concurrency + ".h264", VideoCodec.H264, BenchmarkOperation.AsyncWriterIngestion, concurrency));
        }

        return result.OrderBy(scenario => scenario.Id, StringComparer.Ordinal).ToArray();
    }

    private static BenchmarkScenario Reader(
        string id,
        BenchmarkOperation operation,
        VideoCodec? codec,
        string payload,
        NalShape shape,
        bool events,
        bool dataAccess,
        bool allocationGate)
    {
        const int samples = 8;
        return new BenchmarkScenario(
            id,
            operation,
            codec,
            Mp4WriteMode.Progressive,
            VideoInputKind.None,
            shape,
            payload,
            payload == "aac" ? LargeAacBytes * samples : LargeNalBytes * samples,
            samples,
            shape == NalShape.Multi ? samples * 2 : payload == "aac" ? 0 : samples,
            4,
            events,
            dataAccess,
            "prebuilt-memory",
            allocationGate,
            allocationGate);
    }

    private static BenchmarkScenario Writer(
        string id,
        BenchmarkOperation operation,
        VideoCodec codec,
        Mp4WriteMode layout,
        VideoInputKind input,
        NalShape shape,
        int samples,
        int gop,
        bool required,
        bool allocationGate)
    {
        var logicalBytes = shape == NalShape.Tiny
            ? TinyNalCount * 8
            : checked(samples * LargeNalBytes);
        return new BenchmarkScenario(
            id,
            operation,
            codec,
            layout,
            input,
            shape,
            "video",
            logicalBytes,
            samples,
            shape == NalShape.Tiny ? TinyNalCount : shape == NalShape.Multi ? samples * 2 : samples,
            gop,
            false,
            false,
            "counting-pre-sized-memory",
            required,
            allocationGate);
    }

    private static BenchmarkScenario AsyncReader(
        string id,
        VideoCodec? codec,
        string payload,
        NalShape shape)
    {
        const int samples = 8;
        return new BenchmarkScenario(
            id,
            BenchmarkOperation.AsyncReaderSnapshot,
            codec,
            Mp4WriteMode.Progressive,
            VideoInputKind.None,
            shape,
            payload,
            payload == "aac" ? LargeAacBytes * samples : LargeNalBytes * samples,
            samples,
            shape == NalShape.Multi ? samples * 2 : payload == "aac" ? 0 : samples,
            4,
            false,
            false,
            "async-pre-sized-memory",
            false,
            false,
            IoMode.Async,
            1,
            0);
    }

    private static BenchmarkScenario AsyncWriter(
        string id,
        VideoCodec codec,
        Mp4WriteMode layout,
        VideoInputKind input,
        NalShape shape,
        int samples,
        int gop)
    {
        var logicalBytes = checked(samples * LargeNalBytes);
        return new BenchmarkScenario(
            id,
            layout == Mp4WriteMode.Fragmented ? BenchmarkOperation.AsyncFragmentFlush :
            layout == Mp4WriteMode.FastStart ? BenchmarkOperation.AsyncFastStartFinalization :
            BenchmarkOperation.AsyncWriterIngestion,
            codec,
            layout,
            input,
            shape,
            "video",
            logicalBytes,
            samples,
            shape == NalShape.Multi ? samples * 2 : samples,
            gop,
            false,
            false,
            "async-pre-sized-memory",
            false,
            false,
            IoMode.Async,
            1,
            0);
    }

    private static BenchmarkScenario AsyncFile(
        string id,
        VideoCodec codec,
        BenchmarkOperation operation,
        Mp4WriteMode layout)
    {
        const int samples = 8;
        return new BenchmarkScenario(
            id,
            operation,
            codec,
            layout,
            VideoInputKind.AnnexB,
            NalShape.Multi,
            "video",
            checked(samples * LargeNalBytes),
            samples,
            samples * 2,
            8,
            false,
            false,
            "async-file",
            false,
            false,
            IoMode.Async,
            1,
            0);
    }

    private static BenchmarkScenario AsyncConcurrency(
        string id,
        VideoCodec codec,
        BenchmarkOperation operation,
        int concurrency)
    {
        const int samples = 8;
        return new BenchmarkScenario(
            id,
            operation,
            codec,
            operation == BenchmarkOperation.AsyncWriterIngestion ? Mp4WriteMode.Fragmented : Mp4WriteMode.Progressive,
            VideoInputKind.AnnexB,
            NalShape.Multi,
            "video",
            checked(samples * LargeNalBytes),
            samples,
            samples * 2,
            8,
            false,
            false,
            "async-gated",
            false,
            false,
            IoMode.Async,
            concurrency,
            0);
    }
}

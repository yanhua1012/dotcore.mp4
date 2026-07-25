using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.Method, MethodOrderPolicy.Declared)]
public sealed class Mp4Benchmarks
{
    private byte[]? _readerBytes;
    private CountingMemoryStream? _stream;
    private Mp4Reader? _reader;
    private Mp4Writer? _writer;
    private IReadOnlyList<EncodedVideoNalUnit> _videoSamples = Array.Empty<EncodedVideoNalUnit>();
    private IReadOnlyList<EncodedAudioSample> _audioSamples = Array.Empty<EncodedAudioSample>();
    private long _eventPayloadBytes;

    [ParamsSource(nameof(ScenarioValues))]
    public BenchmarkScenario Scenario { get; set; } = null!;

    public IEnumerable<BenchmarkScenario> ScenarioValues => FixedFixtureMatrix.Scenarios;

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (Scenario.Operation == BenchmarkOperation.ReaderConstructor ||
            Scenario.Operation == BenchmarkOperation.ReaderDelivery)
        {
            _readerBytes = FixedFixtureMatrix.CreateReaderFile(Scenario);
        }
        else
        {
            _videoSamples = FixedFixtureMatrix.CreateWriterVideoSamples(Scenario);
            _audioSamples = FixedFixtureMatrix.CreateWriterAudioSamples(Math.Max(1, Scenario.SampleCount * 2));
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        DisposeIteration();
        _eventPayloadBytes = 0;
        if (Scenario.Operation == BenchmarkOperation.ReaderConstructor)
        {
            _stream = new CountingMemoryStream(_readerBytes!, writable: false);
            return;
        }

        if (Scenario.Operation == BenchmarkOperation.ReaderDelivery)
        {
            _stream = new CountingMemoryStream(_readerBytes!, writable: false);
            _reader = new Mp4Reader(_stream);
            if (Scenario.Events)
            {
                _reader.VideoNalUnitRead += (_, value) => _eventPayloadBytes += value.Data.Length;
                _reader.AacSampleRead += (_, value) => _eventPayloadBytes += value.Data.Length;
            }

            return;
        }

        _stream = new CountingMemoryStream(FixedFixtureMatrix.EstimateOutputCapacity(Scenario));
        _writer = new Mp4Writer(
            _stream,
            new Mp4WriterOptions
            {
                Mode = Scenario.Layout ?? Mp4WriteMode.Progressive,
                MaximumFragmentBufferBytes = 32 * 1024 * 1024
            });
        _writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(Scenario.Codec ?? VideoCodec.H264));

        if (Scenario.Operation == BenchmarkOperation.FragmentFlush)
        {
            foreach (var sample in _videoSamples.Take(Math.Max(1, _videoSamples.Count - 1)))
            {
                _writer.WriteVideoNalUnit(sample);
            }
        }
        else if (Scenario.Operation == BenchmarkOperation.FastStartFinalization)
        {
            _writer.SetAudioCodecConfiguration(FixedFixtureMatrix.AacConfiguration());
            foreach (var sample in _videoSamples) _writer.WriteVideoNalUnit(sample);
            foreach (var sample in _audioSamples) _writer.WriteAudioSample(sample);
        }
    }

    [Benchmark]
    public long Execute()
    {
        switch (Scenario.Operation)
        {
            case BenchmarkOperation.ReaderConstructor:
                _stream!.Position = 0;
                using (var reader = new Mp4Reader(_stream))
                {
                    return reader.VideoConfiguration?.NalLengthSize ??
                           reader.AudioConfiguration?.SampleRate ??
                           0;
                }

            case BenchmarkOperation.ReaderDelivery:
                return DeliverReaderSamples();

            case BenchmarkOperation.WriterIngestion:
                foreach (var sample in _videoSamples) _writer!.WriteVideoNalUnit(sample);
                return _stream!.Length;

            case BenchmarkOperation.FragmentFlush:
                _writer!.WriteVideoNalUnit(_videoSamples[^1]);
                return _stream!.Length;

            case BenchmarkOperation.FastStartFinalization:
                _writer!.FinalizeFile();
                return _stream!.Length;

            default:
                throw new InvalidOperationException("Unknown benchmark operation: " + Scenario.Operation);
        }
    }

    [IterationCleanup]
    public void IterationCleanup() => DisposeIteration();

    [GlobalCleanup]
    public void GlobalCleanup() => DisposeIteration();

    internal static StreamDiagnostics MeasureStreamDiagnostics(BenchmarkScenario scenario)
    {
        var benchmark = new Mp4Benchmarks { Scenario = scenario };
        benchmark.GlobalSetup();
        benchmark.IterationSetup();
        try
        {
            _ = benchmark.Execute();
            return benchmark._stream?.Snapshot() ?? default;
        }
        finally
        {
            benchmark.GlobalCleanup();
        }
    }

    private long DeliverReaderSamples()
    {
        long bytes = 0;
        if (Scenario.Payload == "aac")
        {
            foreach (var sample in _reader!.ReadAudioSamples())
            {
                bytes += Scenario.DataAccess ? sample.Data.Length : 1;
            }
        }
        else
        {
            foreach (var sample in _reader!.ReadVideoNalUnits())
            {
                bytes += Scenario.DataAccess ? sample.Data.Length : 1;
            }
        }

        return bytes + _eventPayloadBytes;
    }

    private void DisposeIteration()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _writer = null;
        _reader = null;
        _stream = null;
    }
}

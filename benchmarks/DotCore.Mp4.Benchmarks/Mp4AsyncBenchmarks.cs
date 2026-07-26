using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

/// <summary>
/// Executes async I/O benchmark scenarios: immediate-completion pre-sized memory,
/// real file-backed streams (FileOptions.Asynchronous) and bounded-concurrency gated
/// streams. The measured region awaits the operation Task before stopping the timer,
/// records synchronous completion observed before the await, and reports async/sync
/// call counts and maximum outstanding I/O.
/// </summary>
internal sealed class Mp4AsyncBenchmarks
{
    private byte[]? _readerBytes;
    private IReadOnlyList<EncodedVideoNalUnit> _videoSamples = Array.Empty<EncodedVideoNalUnit>();
    private IReadOnlyList<EncodedAudioSample> _audioSamples = Array.Empty<EncodedAudioSample>();
    private AsyncCountingStream? _stream;
    private string? _filePath;

    public BenchmarkScenario Scenario { get; set; } = null!;

    public AsyncBenchmarkDiagnostics Diagnostics { get; private set; }

    public void GlobalSetup()
    {
        if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
        {
            _readerBytes = FixedFixtureMatrix.CreateReaderFile(Scenario);
        }
        else
        {
            _videoSamples = FixedFixtureMatrix.CreateWriterVideoSamples(Scenario);
            _audioSamples = FixedFixtureMatrix.CreateWriterAudioSamples(Math.Max(1, Scenario.SampleCount * 2));
        }
    }

    public void IterationSetup()
    {
        Dispose();
        if (Scenario.StreamKind == "async-file")
        {
            _stream = null;
            _filePath = Path.Combine(Path.GetTempPath(), "dotcore-mp4-bench-file-" + Scenario.Id + ".mp4");
            if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
            {
                File.WriteAllBytes(_filePath, _readerBytes!);
            }
            else if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
            return;
        }

        if (Scenario.StreamKind == "async-gated")
        {
            _stream = Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot
                ? new GatedAsyncCountingStream(_readerBytes!, writable: false)
                : new GatedAsyncCountingStream(FixedFixtureMatrix.EstimateOutputCapacity(Scenario));
            return;
        }

        _stream = Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot
            ? new AsyncCountingStream(_readerBytes!, writable: false)
            : new AsyncCountingStream(FixedFixtureMatrix.EstimateOutputCapacity(Scenario));
    }

    public (long Bytes, AsyncBenchmarkDiagnostics Diagnostics) Execute()
    {
        if (Scenario.StreamKind == "async-file")
        {
            return ExecuteFile().GetAwaiter().GetResult();
        }

        if (Scenario.StreamKind == "async-gated")
        {
            return ExecuteConcurrency();
        }

        if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
        {
            return ExecuteReaderSnapshot();
        }

        return ExecuteWriter(_stream!);
    }

    private (long, AsyncBenchmarkDiagnostics) ExecuteReaderSnapshot()
    {
        long bytes;
        var stream = _stream!;
        var token = CancellationToken.None;
        var pending = Mp4Reader.CreateAsync(stream, leaveOpen: true, token);
        var synchronouslyCompleted = pending.IsCompleted;
        using (var reader = pending.GetAwaiter().GetResult())
        {
            bytes = reader.VideoConfiguration?.NalLengthSize ?? reader.AudioConfiguration?.SampleRate ?? 0;
        }

        Diagnostics = Snapshot(stream, completedOperations: 1, synchronouslyCompletedOperations: synchronouslyCompleted ? 1 : 0);
        return (bytes, Diagnostics);
    }

    private (long, AsyncBenchmarkDiagnostics) ExecuteWriter(AsyncCountingStream stream)
    {
        var token = CancellationToken.None;
        var codec = Scenario.Codec ?? VideoCodec.H264;
        var mode = Scenario.Layout ?? Mp4WriteMode.Progressive;
        var pendingWriter = Mp4Writer.CreateAsync(stream, new Mp4WriterOptions { Mode = mode, MaximumFragmentBufferBytes = 32 * 1024 * 1024 }, true, token);
        var synchronouslyCompleted = pendingWriter.IsCompleted;
        using var writer = pendingWriter.GetAwaiter().GetResult();
        writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
        if (mode == Mp4WriteMode.FastStart || Scenario.Operation == BenchmarkOperation.AsyncFragmentFlush)
        {
            writer.SetAudioCodecConfiguration(FixedFixtureMatrix.AacConfiguration());
        }

        if (Scenario.Operation == BenchmarkOperation.AsyncFragmentFlush)
        {
            foreach (var sample in _videoSamples.Take(Math.Max(1, _videoSamples.Count - 1))) writer.WriteVideoNalUnitAsync(sample, token).GetAwaiter().GetResult();
            writer.WriteVideoNalUnitAsync(_videoSamples[^1], token).GetAwaiter().GetResult();
            writer.FinalizeFileAsync(token).GetAwaiter().GetResult();
        }
        else if (Scenario.Operation == BenchmarkOperation.AsyncFastStartFinalization)
        {
            foreach (var sample in _videoSamples) writer.WriteVideoNalUnitAsync(sample, token).GetAwaiter().GetResult();
            foreach (var sample in _audioSamples) writer.WriteAudioSampleAsync(sample, token).GetAwaiter().GetResult();
            writer.FinalizeFileAsync(token).GetAwaiter().GetResult();
        }
        else
        {
            foreach (var sample in _videoSamples) writer.WriteVideoNalUnitAsync(sample, token).GetAwaiter().GetResult();
            writer.FinalizeFileAsync(token).GetAwaiter().GetResult();
        }

        Diagnostics = Snapshot(stream, completedOperations: 1, synchronouslyCompletedOperations: synchronouslyCompleted ? 1 : 0);
        return (stream.Length, Diagnostics);
    }

    private async Task<(long, AsyncBenchmarkDiagnostics)> ExecuteFile()
    {
        var token = CancellationToken.None;
        var codec = Scenario.Codec ?? VideoCodec.H264;
        var path = _filePath!;
        try
        {
            long bytes;
            AsyncBenchmarkDiagnostics diagnostics;
            if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
            {
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
                using (var reader = await Mp4Reader.CreateAsync(file))
                {
                    bytes = reader.VideoConfiguration?.NalLengthSize ?? 0;
                }

                diagnostics = new AsyncBenchmarkDiagnostics(0, 0, 0, 1, 1, "async-file");
            }
            else
            {
                await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
                using (var writer = await Mp4Writer.CreateAsync(file, new Mp4WriterOptions { Mode = Mp4WriteMode.Progressive }))
                {
                    writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
                    foreach (var sample in _videoSamples) await writer.WriteVideoNalUnitAsync(sample);
                    await writer.FinalizeFileAsync();
                    bytes = file.Length;
                }

                diagnostics = new AsyncBenchmarkDiagnostics(0, 0, 0, 1, 1, "async-file");
            }

            Diagnostics = diagnostics;
            return (bytes, diagnostics);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private (long, AsyncBenchmarkDiagnostics) ExecuteConcurrency()
    {
        var token = CancellationToken.None;
        var codec = Scenario.Codec ?? VideoCodec.H264;
        var concurrency = Scenario.Concurrency;
        var shared = new SharedCounter();
        var gate = new ConcurrencyGate(concurrency);
        var streams = new AsyncCountingStream[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            streams[i] = Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot
                ? new BarrierGatedAsyncCountingStream(_readerBytes!, shared, gate, writable: false)
                : new BarrierGatedAsyncCountingStream(FixedFixtureMatrix.EstimateOutputCapacity(Scenario), shared, gate);
        }

        var syncCompleted = 0;
        var tasks = new Task[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            var stream = streams[i];
            if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
            {
                var pending = Mp4Reader.CreateAsync(stream, true, token);
                if (pending.IsCompleted) syncCompleted++;
                tasks[i] = ConsumeReader(pending);
            }
            else
            {
                var pending = Mp4Writer.CreateAsync(stream, new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented, MaximumFragmentBufferBytes = 32 * 1024 * 1024 }, true, token);
                if (pending.IsCompleted) syncCompleted++;
                tasks[i] = IngestFragmented(pending, stream, codec);
            }
        }

        gate.WaitAllEntered();
        var maxOutstanding = shared.Max;
        gate.Release();
        Task.WaitAll(tasks);

        long asyncWrite = 0, asyncRead = 0, syncFallback = 0;
        foreach (var s in streams)
        {
            asyncWrite += s.AsyncWriteCalls;
            asyncRead += s.AsyncReadCalls;
            syncFallback += s.TotalSyncFallback;
        }

        for (var i = 0; i < concurrency; i++) streams[i].Dispose();

        Diagnostics = new AsyncBenchmarkDiagnostics(asyncRead, asyncWrite, syncFallback, concurrency, syncCompleted, "async-gated", maxOutstanding);
        return (asyncWrite + asyncRead, Diagnostics);
    }

    private static async Task ConsumeReader(Task<Mp4Reader> pending)
    {
        using var reader = await pending.ConfigureAwait(false);
        _ = reader.VideoConfiguration?.NalLengthSize ?? reader.AudioConfiguration?.SampleRate ?? 0;
    }

    private async Task IngestFragmented(Task<Mp4Writer> pending, AsyncCountingStream stream, VideoCodec codec)
    {
        using var writer = await pending.ConfigureAwait(false);
        writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
        foreach (var sample in _videoSamples) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
        await writer.FinalizeFileAsync().ConfigureAwait(false);
        _ = stream.Length;
    }

    private static AsyncBenchmarkDiagnostics Snapshot(
        AsyncCountingStream stream,
        long completedOperations,
        long synchronouslyCompletedOperations)
    {
        return new AsyncBenchmarkDiagnostics(
            stream.AsyncReadCalls,
            stream.AsyncWriteCalls,
            stream.TotalSyncFallback,
            completedOperations,
            synchronouslyCompletedOperations,
            stream.GetType().Name);
    }

    public void Dispose() => _stream?.Dispose();
}

internal readonly record struct AsyncBenchmarkDiagnostics(
    long AsyncReadCalls,
    long AsyncWriteCalls,
    long SyncFallbackCalls,
    long CompletedOperations,
    long SynchronouslyCompletedOperations,
    string StreamKind,
    long MaxOutstandingIo = 0);
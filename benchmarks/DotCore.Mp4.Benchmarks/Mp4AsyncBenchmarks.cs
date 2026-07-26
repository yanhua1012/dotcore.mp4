using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

/// <summary>
/// Executes async I/O benchmark scenarios: immediate-completion pre-sized memory,
/// real file-backed streams (FileOptions.Asynchronous) and bounded-concurrency gated
/// streams. The measured region awaits the operation Task before stopping the timer,
/// records synchronous completion observed before the await, and reports observed
/// (never synthesized) async/sync call counts, bytes moved and maximum outstanding I/O.
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

    public Task<(long Bytes, AsyncBenchmarkDiagnostics Diagnostics)> ExecuteAsync()
    {
        if (Scenario.StreamKind == "async-file")
        {
            return ExecuteFileAsync();
        }

        if (Scenario.StreamKind == "async-gated")
        {
            return ExecuteConcurrencyAsync();
        }

        if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
        {
            return ExecuteReaderSnapshotAsync();
        }

        return ExecuteWriterAsync(_stream!);
    }

    private async Task<(long, AsyncBenchmarkDiagnostics)> ExecuteReaderSnapshotAsync()
    {
        var stream = _stream!;
        var pending = Mp4Reader.CreateAsync(stream, leaveOpen: true, CancellationToken.None);
        var synchronouslyCompleted = pending.IsCompleted;
        long bytes;
        using (var reader = await pending.ConfigureAwait(false))
        {
            bytes = reader.VideoConfiguration?.NalLengthSize ?? reader.AudioConfiguration?.SampleRate ?? 0;
        }

        Diagnostics = Snapshot(stream, completedOperations: 1, synchronouslyCompletedOperations: synchronouslyCompleted ? 1 : 0);
        return (bytes, Diagnostics);
    }

    private async Task<(long, AsyncBenchmarkDiagnostics)> ExecuteWriterAsync(AsyncCountingStream stream)
    {
        var codec = Scenario.Codec ?? VideoCodec.H264;
        var mode = Scenario.Layout ?? Mp4WriteMode.Progressive;
        var pendingWriter = Mp4Writer.CreateAsync(stream, new Mp4WriterOptions { Mode = mode, MaximumFragmentBufferBytes = 32 * 1024 * 1024 }, true, CancellationToken.None);
        var synchronouslyCompleted = pendingWriter.IsCompleted;
        using var writer = await pendingWriter.ConfigureAwait(false);
        writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
        if (mode == Mp4WriteMode.FastStart || Scenario.Operation == BenchmarkOperation.AsyncFragmentFlush)
        {
            writer.SetAudioCodecConfiguration(FixedFixtureMatrix.AacConfiguration());
        }

        if (Scenario.Operation == BenchmarkOperation.AsyncFragmentFlush)
        {
            foreach (var sample in _videoSamples.Take(Math.Max(1, _videoSamples.Count - 1))) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
            await writer.WriteVideoNalUnitAsync(_videoSamples[^1]).ConfigureAwait(false);
            await writer.FinalizeFileAsync().ConfigureAwait(false);
        }
        else if (Scenario.Operation == BenchmarkOperation.AsyncFastStartFinalization)
        {
            foreach (var sample in _videoSamples) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
            foreach (var sample in _audioSamples) await writer.WriteAudioSampleAsync(sample).ConfigureAwait(false);
            await writer.FinalizeFileAsync().ConfigureAwait(false);
        }
        else
        {
            foreach (var sample in _videoSamples) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
            await writer.FinalizeFileAsync().ConfigureAwait(false);
        }

        Diagnostics = Snapshot(stream, completedOperations: 1, synchronouslyCompletedOperations: synchronouslyCompleted ? 1 : 0);
        return (stream.Length, Diagnostics);
    }

    private async Task<(long, AsyncBenchmarkDiagnostics)> ExecuteFileAsync()
    {
        var codec = Scenario.Codec ?? VideoCodec.H264;
        var path = _filePath!;
        var mode = Scenario.Layout ?? Mp4WriteMode.Progressive;
        try
        {
            long bytes;
            AsyncBenchmarkDiagnostics diagnostics;
            if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
            {
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                var counting = new AsyncCountingFileStream(file);
                long asyncReadCalls;
                long asyncReadBytes;
                long syncFallback;
                try
                {
                    using (var reader = await Mp4Reader.CreateAsync(counting).ConfigureAwait(false))
                    {
                        bytes = reader.VideoConfiguration?.NalLengthSize ?? 0;
                    }
                    asyncReadCalls = counting.AsyncReadCalls;
                    asyncReadBytes = counting.AsyncReadBytes;
                    syncFallback = counting.TotalSyncFallback;
                }
                finally
                {
                    counting.Dispose();
                }

                diagnostics = new AsyncBenchmarkDiagnostics(asyncReadCalls, 0, syncFallback, 1, 1, "async-file", asyncReadBytes);
            }
            else
            {
                var access = mode == Mp4WriteMode.FastStart ? FileAccess.ReadWrite : FileAccess.Write;
                var file = new FileStream(path, FileMode.Create, access, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                var counting = new AsyncCountingFileStream(file);
                long asyncWriteCalls;
                long asyncWriteBytes;
                long asyncReadCalls;
                long asyncReadBytes;
                long syncFallback;
                try
                {
                    using (var writer = await Mp4Writer.CreateAsync(counting, new Mp4WriterOptions { Mode = mode, MaximumFragmentBufferBytes = 32 * 1024 * 1024 }).ConfigureAwait(false))
                    {
                        writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
                        if (mode == Mp4WriteMode.FastStart || Scenario.Operation == BenchmarkOperation.AsyncFragmentFlush)
                        {
                            writer.SetAudioCodecConfiguration(FixedFixtureMatrix.AacConfiguration());
                        }

                        if (Scenario.Operation == BenchmarkOperation.AsyncFragmentFlush)
                        {
                            foreach (var sample in _videoSamples.Take(Math.Max(1, _videoSamples.Count - 1))) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
                            await writer.WriteVideoNalUnitAsync(_videoSamples[^1]).ConfigureAwait(false);
                        }
                        else if (Scenario.Operation == BenchmarkOperation.AsyncFastStartFinalization)
                        {
                            foreach (var sample in _videoSamples) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
                            foreach (var sample in _audioSamples) await writer.WriteAudioSampleAsync(sample).ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (var sample in _videoSamples) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
                        }

                        await writer.FinalizeFileAsync().ConfigureAwait(false);
                        bytes = counting.Length;
                        asyncWriteCalls = counting.AsyncWriteCalls;
                        asyncWriteBytes = counting.AsyncWriteBytes;
                        asyncReadCalls = counting.AsyncReadCalls;
                        asyncReadBytes = counting.AsyncReadBytes;
                        syncFallback = counting.TotalSyncFallback;
                    }
                }
                finally
                {
                    counting.Dispose();
                }

                diagnostics = new AsyncBenchmarkDiagnostics(asyncReadCalls, asyncWriteCalls, syncFallback, 1, 1, "async-file", asyncWriteBytes + asyncReadBytes);
            }

            Diagnostics = diagnostics;
            return (bytes, diagnostics);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<(long, AsyncBenchmarkDiagnostics)> ExecuteConcurrencyAsync()
    {
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
        var videoSamples = _videoSamples;
        for (var i = 0; i < concurrency; i++)
        {
            var stream = streams[i];
            if (Scenario.Operation == BenchmarkOperation.AsyncReaderSnapshot)
            {
                var pending = Mp4Reader.CreateAsync(stream, true, CancellationToken.None);
                if (pending.IsCompleted) syncCompleted++;
                tasks[i] = ConsumeReaderAsync(pending);
            }
            else
            {
                var pending = Mp4Writer.CreateAsync(stream, new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented, MaximumFragmentBufferBytes = 32 * 1024 * 1024 }, true, CancellationToken.None);
                if (pending.IsCompleted) syncCompleted++;
                tasks[i] = IngestFragmentedAsync(pending, stream, codec, videoSamples);
            }
        }

        gate.WaitAllEntered();
        var maxOutstanding = shared.Max;
        gate.Release();
        await Task.WhenAll(tasks).ConfigureAwait(false);

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

    private static async Task ConsumeReaderAsync(Task<Mp4Reader> pending)
    {
        using var reader = await pending.ConfigureAwait(false);
        _ = reader.VideoConfiguration?.NalLengthSize ?? reader.AudioConfiguration?.SampleRate ?? 0;
    }

    private static async Task IngestFragmentedAsync(Task<Mp4Writer> pending, AsyncCountingStream stream, VideoCodec codec, IReadOnlyList<EncodedVideoNalUnit> videoSamples)
    {
        using var writer = await pending.ConfigureAwait(false);
        writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
        foreach (var sample in videoSamples) await writer.WriteVideoNalUnitAsync(sample).ConfigureAwait(false);
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

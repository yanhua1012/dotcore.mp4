using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4.Benchmarks;

/// <summary>
/// Pre-sized memory stream that counts async read/write calls, synchronous fallback
/// read/write calls and the maximum number of concurrently outstanding async I/O
/// operations. Used by the async benchmark families to prove virtual async dispatch,
/// zero synchronous fallback and bounded concurrency without <see cref="Thread.Sleep"/>.
/// </summary>
internal class AsyncCountingStream : MemoryStream
{
    private readonly SharedCounter? _shared;
    private int _outstanding;
    private int _maxOutstanding;

    public AsyncCountingStream(int capacity) : base(capacity) { }
    public AsyncCountingStream(byte[] buffer, bool writable = false) : base(buffer, writable) { }
    public AsyncCountingStream(int capacity, SharedCounter shared) : base(capacity) { _shared = shared; }
    public AsyncCountingStream(byte[] buffer, SharedCounter shared, bool writable = false) : base(buffer, writable) { _shared = shared; }

    public long AsyncReadCalls { get; protected set; }
    public long AsyncWriteCalls { get; protected set; }
    public long SyncReadFallbackCalls { get; private set; }
    public long SyncWriteFallbackCalls { get; private set; }
    public long MaxOutstandingIo => _maxOutstanding;

    public override int Read(byte[] buffer, int offset, int count)
    {
        SyncReadFallbackCalls++;
        return base.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        SyncWriteFallbackCalls++;
        base.Write(buffer, offset, count);
    }

    protected int ReadCore(byte[] buffer, int offset, int count) => base.Read(buffer, offset, count);
    protected void WriteCore(byte[] buffer, int offset, int count) => base.Write(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnterOutstanding();
        try
        {
            AsyncReadCalls++;
            var read = ReadCore(buffer, offset, count);
            return Task.FromResult(read);
        }
        finally
        {
            ExitOutstanding();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnterOutstanding();
        try
        {
            AsyncWriteCalls++;
            WriteCore(buffer, offset, count);
            return Task.CompletedTask;
        }
        finally
        {
            ExitOutstanding();
        }
    }

    public long TotalSyncFallback => SyncReadFallbackCalls + SyncWriteFallbackCalls;

    protected void EnterOutstanding()
    {
        var current = Interlocked.Increment(ref _outstanding);
        if (current > _maxOutstanding) _maxOutstanding = current;
        if (_shared != null) _shared.Increment();
    }

    protected void ExitOutstanding()
    {
        Interlocked.Decrement(ref _outstanding);
        _shared?.Decrement();
    }
}

/// <summary>
/// Async-counting stream that completes every async operation asynchronously (via
/// <see cref="Task.Yield"/>) so that bounded-concurrency scenarios actually keep the
/// declared number of operations in-flight, proving maximum outstanding I/O without
/// <see cref="Thread.Sleep"/>. Synchronous read/write still count as fallback.
/// </summary>
internal sealed class GatedAsyncCountingStream : AsyncCountingStream
{
    public GatedAsyncCountingStream(int capacity) : base(capacity) { }
    public GatedAsyncCountingStream(byte[] buffer, bool writable = false) : base(buffer, writable) { }
    public GatedAsyncCountingStream(int capacity, SharedCounter shared) : base(capacity, shared) { }
    public GatedAsyncCountingStream(byte[] buffer, SharedCounter shared, bool writable = false) : base(buffer, shared, writable) { }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class BarrierGatedAsyncCountingStream : AsyncCountingStream
{
    private readonly ConcurrencyGate _gate;

    public BarrierGatedAsyncCountingStream(byte[] buffer, SharedCounter shared, ConcurrencyGate gate, bool writable = false)
        : base(buffer, shared, writable)
    {
        _gate = gate;
    }

    public BarrierGatedAsyncCountingStream(int capacity, SharedCounter shared, ConcurrencyGate gate)
        : base(capacity, shared)
    {
        _gate = gate;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnterOutstanding();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            AsyncReadCalls++;
            return ReadCore(buffer, offset, count);
        }
        finally
        {
            ExitOutstanding();
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        EnterOutstanding();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            AsyncWriteCalls++;
            WriteCore(buffer, offset, count);
        }
        finally
        {
            ExitOutstanding();
        }
    }
}

internal sealed class SharedCounter
{
    private int _current;
    private int _max;
    public void Increment()
    {
        var c = Interlocked.Increment(ref _current);
        if (c > _max) Interlocked.Exchange(ref _max, c);
    }
    public void Decrement() => Interlocked.Decrement(ref _current);
    public int Max => _max;
}

/// <summary>
/// Wraps a real <see cref="FileStream"/> opened with <see cref="FileOptions.Asynchronous"/> and
/// records async read/write call counts, bytes moved and synchronous fallback calls, so the
/// file-backed async benchmark family reports observed (not synthesized) async I/O metrics.
/// </summary>
internal sealed class AsyncCountingFileStream : Stream
{
    private readonly FileStream _inner;
    private long _asyncReadCalls;
    private long _asyncWriteCalls;
    private long _asyncReadBytes;
    private long _asyncWriteBytes;
    private long _syncReadFallback;
    private long _syncWriteFallback;

    public AsyncCountingFileStream(FileStream inner) { _inner = inner; }

    public long AsyncReadCalls => _asyncReadCalls;
    public long AsyncWriteCalls => _asyncWriteCalls;
    public long AsyncReadBytes => _asyncReadBytes;
    public long AsyncWriteBytes => _asyncWriteBytes;
    public long TotalSyncFallback => _syncReadFallback + _syncWriteFallback;
    public long MaxOutstandingIo => 0;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _syncReadFallback++;
        return _inner.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _syncWriteFallback++;
        _inner.Write(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncReadCalls++;
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _asyncReadBytes += read;
        return read;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncWriteCalls++;
        _asyncWriteBytes += count;
        await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Coordinates bounded-concurrency scenarios so that all declared operations are
/// genuinely in-flight before any completes. Each operation enters the gate, awaits a
/// shared <see cref="TaskCompletionSource{TResult}"/>, and the dispatcher releases the
/// gate only after every operation has entered. This makes the observed maximum
/// in-flight equal the scenario concurrency without <see cref="Thread.Sleep"/>.
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;
    private readonly int _expected;
    private readonly ManualResetEventSlim _allEntered;

    public ConcurrencyGate(int expected)
    {
        _expected = expected;
        _allEntered = new ManualResetEventSlim(false);
    }

    public Task WaitAsync()
    {
        if (Interlocked.Increment(ref _entered) >= _expected) _allEntered.Set();
        return _gate.Task;
    }

    public void WaitAllEntered() => _allEntered.Wait();
    public void Release() => _gate.TrySetResult(true);
}
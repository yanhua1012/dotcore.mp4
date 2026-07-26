using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4.Tests;

/// <summary>
/// Instruments a caller stream so synchronous <see cref="Read"/>/<see cref="Write"/> fail,
/// while async overrides complete through deterministic <see cref="TaskCompletionSource{T}"/>
/// gates. Records observed tokens, call counts, byte counts and the maximum number of
/// outstanding async operations, without relying on <see cref="Thread.Sleep"/>.
/// </summary>
internal sealed class AsyncOnlyGateStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly bool _seekable;
    private readonly bool _writable;
    private readonly bool _readable;
    private readonly List<CancellationToken> _observedTokens = new();
    private readonly List<long> _readOffsets = new();
    private readonly List<int> _readCounts = new();
    private readonly List<long> _writeOffsets = new();
    private readonly List<int> _writeCounts = new();

    private TaskCompletionSource<bool>? _readGate;
    private TaskCompletionSource<bool>? _writeGate;
    private int _outstanding;
    private int _maximumOutstanding;
    private long _syncReadCalls;
    private long _syncWriteCalls;
    private long _asyncReadCalls;
    private long _asyncWriteCalls;
    private long _asyncReadBytes;
    private long _asyncWriteBytes;
    private bool _throwAfterByteOnRead;
    private int _throwAfterByteCount;
    private bool _throwOnWrite;
    private bool _closed;

    public AsyncOnlyGateStream(byte[]? initial = null, bool seekable = true, bool writable = true, bool readable = true)
    {
        _inner = initial == null ? new MemoryStream() : new MemoryStream(initial);
        _seekable = seekable;
        _writable = writable;
        _readable = readable;
    }

    public IReadOnlyList<CancellationToken> ObservedTokens => _observedTokens;
    public IReadOnlyList<long> ReadOffsets => _readOffsets;
    public IReadOnlyList<int> ReadCounts => _readCounts;
    public IReadOnlyList<long> WriteOffsets => _writeOffsets;
    public IReadOnlyList<int> WriteCounts => _writeCounts;
    public long SyncReadCalls => _syncReadCalls;
    public long SyncWriteCalls => _syncWriteCalls;
    public long AsyncReadCalls => _asyncReadCalls;
    public long AsyncWriteCalls => _asyncWriteCalls;
    public long AsyncReadBytes => _asyncReadBytes;
    public long AsyncWriteBytes => _asyncWriteBytes;
    public int MaximumOutstanding => _maximumOutstanding;
    public bool IsClosed => _closed;

    public void ReleaseReadGate() => Complete(ref _readGate);
    public void ReleaseWriteGate() => Complete(ref _writeGate);

    public TaskCompletionSource<bool> ArmReadGate()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readGate = gate;
        return gate;
    }

    public TaskCompletionSource<bool> ArmWriteGate()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writeGate = gate;
        return gate;
    }

    /// <summary>Configures the next async read to throw after the given number of bytes.</summary>
    public void ThrowAfterByteOnRead(int byteCount)
    {
        _throwAfterByteOnRead = true;
        _throwAfterByteCount = byteCount;
    }

    public void ArmThrowOnWrite() => _throwOnWrite = true;

    public byte[] ToArray() => _inner.ToArray();
    public long InnerLength => _inner.Length;
    public long InnerPosition => _inner.Position;

    public override bool CanRead => _readable;
    public override bool CanSeek => _seekable;
    public override bool CanWrite => _writable;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _syncReadCalls++;
        throw new InvalidOperationException(
            "AsyncOnlyGateStream rejects synchronous reads to prove production uses async dispatch.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _syncWriteCalls++;
        throw new InvalidOperationException(
            "AsyncOnlyGateStream rejects synchronous writes to prove production uses async dispatch.");
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _observedTokens.Add(cancellationToken);
        _readOffsets.Add(_inner.Position);
        _readCounts.Add(count);
        _asyncReadCalls++;
        return ReadAsyncCore(buffer, offset, count, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _observedTokens.Add(cancellationToken);
        _writeOffsets.Add(_inner.Position);
        _writeCounts.Add(count);
        _asyncWriteCalls++;
        return WriteAsyncCore(buffer, offset, count, cancellationToken);
    }

    private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await EnterOutstandingAsync(_readGate, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_throwAfterByteOnRead && _throwAfterByteCount >= 0)
            {
                var partial = Math.Min(count, _throwAfterByteCount);
                _inner.Read(buffer, offset, partial);
                _asyncReadBytes += partial;
                _throwAfterByteOnRead = false;
                throw new IOException("AsyncOnlyGateStream injected read failure after partial bytes.");
            }

            var read = _inner.Read(buffer, offset, count);
            _asyncReadBytes += read;
            return read;
        }
        finally
        {
            ExitOutstanding();
        }
    }

    private async Task WriteAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await EnterOutstandingAsync(_writeGate, cancellationToken).ConfigureAwait(false);
        try
        {
            if (_throwOnWrite)
            {
                _throwOnWrite = false;
                throw new IOException("AsyncOnlyGateStream injected write failure.");
            }

            _inner.Write(buffer, offset, count);
            _asyncWriteBytes += count;
        }
        finally
        {
            ExitOutstanding();
        }
    }

    private async Task EnterOutstandingAsync(TaskCompletionSource<bool>? gate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gate != null)
        {
            await gate.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var current = Interlocked.Increment(ref _outstanding);
        if (current > _maximumOutstanding) _maximumOutstanding = current;
    }

    private void ExitOutstanding()
    {
        Interlocked.Decrement(ref _outstanding);
    }

    private static void Complete(ref TaskCompletionSource<bool>? gate)
    {
        var captured = Interlocked.Exchange(ref gate, null);
        captured?.TrySetResult(true);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _closed = true;
        base.Dispose(disposing);
        _inner.Dispose();
    }
}

/// <summary>
/// Seekable file-like stream whose only difference from <see cref="MemoryStream"/> is that
/// synchronous <see cref="Read"/>/<see cref="Write"/> fail while async overrides complete
/// immediately. Used to validate the writer's async path without <see cref="Thread.Sleep"/>.
/// </summary>
internal sealed class SeekableAsyncOnlyStream : Stream
{
    private readonly MemoryStream _inner;
    private long _syncReadCalls;
    private long _syncWriteCalls;
    private long _asyncReadCalls;
    private long _asyncWriteCalls;

    public SeekableAsyncOnlyStream() { _inner = new MemoryStream(); }
    public SeekableAsyncOnlyStream(byte[] initial) { _inner = new MemoryStream(initial); }

    public long SyncReadCalls => _syncReadCalls;
    public long SyncWriteCalls => _syncWriteCalls;
    public long AsyncReadCalls => _asyncReadCalls;
    public long AsyncWriteCalls => _asyncWriteCalls;
    public byte[] ToArray() => _inner.ToArray();

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _syncReadCalls++;
        throw new InvalidOperationException("SeekableAsyncOnlyStream rejects synchronous reads.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _syncWriteCalls++;
        throw new InvalidOperationException("SeekableAsyncOnlyStream rejects synchronous writes.");
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncReadCalls++;
        _inner.Read(buffer, offset, count);
        return Task.FromResult(count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncWriteCalls++;
        _inner.Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
}

/// <summary>
/// Writable non-seekable stream whose synchronous <see cref="Write"/> fails while async
/// writes complete immediately. Used to exercise the fragmented non-seekable output path.
/// </summary>
internal sealed class NonSeekableAsyncWriteStream : Stream
{
    private readonly MemoryStream _inner;
    private long _syncWriteCalls;
    private long _asyncWriteCalls;
    private long _asyncWriteBytes;

    public NonSeekableAsyncWriteStream() { _inner = new MemoryStream(); }

    public long SyncWriteCalls => _syncWriteCalls;
    public long AsyncWriteCalls => _asyncWriteCalls;
    public long AsyncWriteBytes => _asyncWriteBytes;
    public byte[] ToArray() => _inner.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _syncWriteCalls++;
        throw new InvalidOperationException("NonSeekableAsyncWriteStream rejects synchronous writes.");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncWriteCalls++;
        _asyncWriteBytes += count;
        _inner.Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Flush() { }
}
using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4.Benchmarks;

/// <summary>
/// 計數非同步與同步 Read/Write 呼叫次數以及最大在途非同步 I/O 操作數的記憶體串流。
/// 用於非同步基準測試家族，以驗證虛擬非同步派發、零同步降級與無 Sleep 的受控並行度。
/// </summary>
internal class AsyncCountingStream : MemoryStream
{
    private readonly SharedCounter? _shared;
    private int _outstanding;
    private int _maxOutstanding;

    /// <summary>
    /// 初始化指定容量的新 <see cref="AsyncCountingStream"/> 實例。
    /// </summary>
    public AsyncCountingStream(int capacity) : base(capacity) { }

    /// <summary>
    /// 使用既有緩衝區初始化新 <see cref="AsyncCountingStream"/> 實例。
    /// </summary>
    public AsyncCountingStream(byte[] buffer, bool writable = false) : base(buffer, writable) { }

    /// <summary>
    /// 初始化包含共享計數器與指定容量的新 <see cref="AsyncCountingStream"/> 實例。
    /// </summary>
    public AsyncCountingStream(int capacity, SharedCounter shared) : base(capacity) { _shared = shared; }

    /// <summary>
    /// 使用既有緩衝區與共享計數器初始化新 <see cref="AsyncCountingStream"/> 實例。
    /// </summary>
    public AsyncCountingStream(byte[] buffer, SharedCounter shared, bool writable = false) : base(buffer, writable) { _shared = shared; }

    /// <summary>
    /// 取得非同步讀取呼叫次數。
    /// </summary>
    public long AsyncReadCalls { get; protected set; }

    /// <summary>
    /// 取得非同步寫入呼叫次數。
    /// </summary>
    public long AsyncWriteCalls { get; protected set; }

    /// <summary>
    /// 取得同步讀取降級呼叫次數。
    /// </summary>
    public long SyncReadFallbackCalls { get; private set; }

    /// <summary>
    /// 取得同步寫入降級呼叫次數。
    /// </summary>
    public long SyncWriteFallbackCalls { get; private set; }

    /// <summary>
    /// 取得最大在途非同步 I/O 操作數。
    /// </summary>
    public long MaxOutstandingIo => _maxOutstanding;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        SyncReadFallbackCalls++;
        return base.Read(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        SyncWriteFallbackCalls++;
        base.Write(buffer, offset, count);
    }

    /// <summary>

    /// 核心同步讀取邏輯。

    /// </summary>
    protected int ReadCore(byte[] buffer, int offset, int count) => base.Read(buffer, offset, count);

    /// <summary>

    /// 核心同步寫入邏輯。

    /// </summary>
    protected void WriteCore(byte[] buffer, int offset, int count) => base.Write(buffer, offset, count);

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <summary>
    /// 取得總同步降級呼叫次數。
    /// </summary>
    public long TotalSyncFallback => SyncReadFallbackCalls + SyncWriteFallbackCalls;

    /// <summary>

    /// 進入在途操作計數。

    /// </summary>
    protected void EnterOutstanding()
    {
        var current = Interlocked.Increment(ref _outstanding);
        if (current > _maxOutstanding) _maxOutstanding = current;
        if (_shared != null) _shared.Increment();
    }

    /// <summary>

    /// 離開在途操作計數。

    /// </summary>
    protected void ExitOutstanding()
    {
        Interlocked.Decrement(ref _outstanding);
        _shared?.Decrement();
    }
}

/// <summary>
/// 透過 <see cref="Task.Yield"/> 將每個非同步操作非同步化的計數串流，用於驗證多操作並行發射時的最大在途數。
/// </summary>
internal sealed class GatedAsyncCountingStream : AsyncCountingStream
{
    /// <summary>
    /// 初始化指定容量的新實例。
    /// </summary>
    public GatedAsyncCountingStream(int capacity) : base(capacity) { }
    /// <summary>
    /// 使用既有緩衝區初始化新實例。
    /// </summary>
    public GatedAsyncCountingStream(byte[] buffer, bool writable = false) : base(buffer, writable) { }
    /// <summary>
    /// 初始化包含共享計數器與指定容量的新實例。
    /// </summary>
    public GatedAsyncCountingStream(int capacity, SharedCounter shared) : base(capacity, shared) { }
    /// <summary>
    /// 使用既有緩衝區與共享計數器初始化新實例。
    /// </summary>
    public GatedAsyncCountingStream(byte[] buffer, SharedCounter shared, bool writable = false) : base(buffer, shared, writable) { }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 結合 <see cref="ConcurrencyGate"/> 柵欄閘門的非同步計數串流，確保持續累積到預期並行數後才統一釋放。
/// </summary>
internal sealed class BarrierGatedAsyncCountingStream : AsyncCountingStream
{
    private readonly ConcurrencyGate _gate;

    /// <summary>

    /// 使用既有緩衝區、共享計數器與閘門初始化新實例。

    /// </summary>
    public BarrierGatedAsyncCountingStream(byte[] buffer, SharedCounter shared, ConcurrencyGate gate, bool writable = false)
        : base(buffer, shared, writable)
    {
        _gate = gate;
    }

    /// <summary>

    /// 初始化包含容量、共享計數器與閘門的新實例。

    /// </summary>
    public BarrierGatedAsyncCountingStream(int capacity, SharedCounter shared, ConcurrencyGate gate)
        : base(capacity, shared)
    {
        _gate = gate;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

/// <summary>
/// 用於跨多個串流追蹤最大並行在途數的共享計數器。
/// </summary>
internal sealed class SharedCounter
{
    private int _current;
    private int _max;

    /// <summary>

    /// 遞增並更新峰值。

    /// </summary>
    public void Increment()
    {
        var c = Interlocked.Increment(ref _current);
        if (c > _max) Interlocked.Exchange(ref _max, c);
    }

    /// <summary>

    /// 遞減當前在途數。

    /// </summary>
    public void Decrement() => Interlocked.Decrement(ref _current);

    /// <summary>

    /// 取得觀測到的最大並行在途數。

    /// </summary>
    public int Max => _max;
}

/// <summary>
/// 包裹以 <see cref="FileOptions.Asynchronous"/> 開啟的真實 <see cref="FileStream"/>，
/// 並記錄非同步 Read/Write 呼叫數、傳輸位元組數與同步降級呼叫，以收集檔案非同步 I/O 指標。
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

    /// <summary>
    /// 初始化 <see cref="AsyncCountingFileStream"/> 類別的新實例。
    /// </summary>
    public AsyncCountingFileStream(FileStream inner) { _inner = inner; }

    /// <summary>

    /// 取得非同步讀取呼叫數。

    /// </summary>
    public long AsyncReadCalls => _asyncReadCalls;

    /// <summary>

    /// 取得非同步寫入呼叫數。

    /// </summary>
    public long AsyncWriteCalls => _asyncWriteCalls;

    /// <summary>

    /// 取得非同步讀取位元組數。

    /// </summary>
    public long AsyncReadBytes => _asyncReadBytes;

    /// <summary>

    /// 取得非同步寫入位元組數。

    /// </summary>
    public long AsyncWriteBytes => _asyncWriteBytes;

    /// <summary>

    /// 取得總同步降級呼叫數。

    /// </summary>
    public long TotalSyncFallback => _syncReadFallback + _syncWriteFallback;

    /// <summary>

    /// 取得最大在途操作數 (檔案串流固定為 0)。

    /// </summary>
    public long MaxOutstandingIo => 0;

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;
    /// <inheritdoc/>
    public override bool CanSeek => _inner.CanSeek;
    /// <inheritdoc/>
    public override bool CanWrite => _inner.CanWrite;
    /// <inheritdoc/>
    public override long Length => _inner.Length;
    /// <inheritdoc/>
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        _syncReadFallback++;
        return _inner.Read(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        _syncWriteFallback++;
        _inner.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncReadCalls++;
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _asyncReadBytes += read;
        return read;
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _asyncWriteCalls++;
        _asyncWriteBytes += count;
        await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Flush() => _inner.Flush();
    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    /// <inheritdoc/>
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// 協調受控並行情境，使所有預期操作在任一操作完成前均真正維持在途狀態。
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;
    private readonly int _expected;
    private readonly ManualResetEventSlim _allEntered;

    /// <summary>
    /// 初始化指定預期並行數的 <see cref="ConcurrencyGate"/> 實例。
    /// </summary>
    public ConcurrencyGate(int expected)
    {
        _expected = expected;
        _allEntered = new ManualResetEventSlim(false);
    }

    /// <summary>

    /// 進入並等待柵欄釋放。

    /// </summary>
    public Task WaitAsync()
    {
        if (Interlocked.Increment(ref _entered) >= _expected) _allEntered.Set();
        return _gate.Task;
    }

    /// <summary>

    /// 等待所有預期操作皆已進入柵欄。

    /// </summary>
    public void WaitAllEntered() => _allEntered.Wait();

    /// <summary>

    /// 釋放柵欄，允許所有在途操作繼續執行。

    /// </summary>
    public void Release() => _gate.TrySetResult(true);
}
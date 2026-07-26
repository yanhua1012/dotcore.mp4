namespace DotCore.Mp4.Benchmarks;

/// <summary>
/// 記錄同步 Read、Write、Seek、SetLength 呼叫次數與位元組移動量的記憶體串流。
/// </summary>
internal sealed class CountingMemoryStream : MemoryStream
{
    /// <summary>
    /// 初始化指定初始容量的新 <see cref="CountingMemoryStream"/> 實例。
    /// </summary>
    public CountingMemoryStream(int capacity)
        : base(capacity)
    {
    }

    /// <summary>
    /// 使用既有位元組緩衝區初始化新 <see cref="CountingMemoryStream"/> 實例。
    /// </summary>
    public CountingMemoryStream(byte[] buffer, bool writable = false)
        : base(buffer, writable)
    {
    }

    /// <summary>

    /// 取得 Read 呼叫次數。

    /// </summary>
    public long ReadCalls { get; private set; }

    /// <summary>

    /// 取得 Write 呼叫次數。

    /// </summary>
    public long WriteCalls { get; private set; }

    /// <summary>

    /// 取得 Seek 呼叫次數。

    /// </summary>
    public long SeekCalls { get; private set; }

    /// <summary>

    /// 取得 SetLength 呼叫次數。

    /// </summary>
    public long SetLengthCalls { get; private set; }

    /// <summary>

    /// 取得讀取總位元組數。

    /// </summary>
    public long BytesRead { get; private set; }

    /// <summary>

    /// 取得寫入總位元組數。

    /// </summary>
    public long BytesWritten { get; private set; }

    /// <summary>

    /// 取得緩衝區重新分配容量擴增次數。

    /// </summary>
    public long GrowthEvents { get; private set; }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;
        var read = base.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        var read = base.Read(buffer);
        BytesRead += read;
        return read;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin loc)
    {
        SeekCalls++;
        return base.Seek(offset, loc);
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        SetLengthCalls++;
        if (value > Capacity) GrowthEvents++;
        base.SetLength(value);
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        CountWrite(count);
        base.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CountWrite(buffer.Length);
        base.Write(buffer);
    }

    /// <summary>
    /// 取得串流診斷數據快照。
    /// </summary>
    public StreamDiagnostics Snapshot()
    {
        return new StreamDiagnostics(
            WriteCalls,
            ReadCalls,
            SeekCalls,
            SetLengthCalls,
            BytesWritten,
            GrowthEvents);
    }

    private void CountWrite(int count)
    {
        WriteCalls++;
        BytesWritten += count;
        if (Position + count > Capacity) GrowthEvents++;
    }
}

/// <summary>
/// 串流診斷統計數據結構。
/// </summary>
internal readonly record struct StreamDiagnostics(
    long WriteCalls,
    long ReadCalls,
    long SeekCalls,
    long SetLengthCalls,
    long BytesWritten,
    long GrowthEvents);

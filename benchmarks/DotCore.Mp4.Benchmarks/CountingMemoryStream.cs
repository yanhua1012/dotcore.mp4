namespace DotCore.Mp4.Benchmarks;

internal sealed class CountingMemoryStream : MemoryStream
{
    public CountingMemoryStream(int capacity)
        : base(capacity)
    {
    }

    public CountingMemoryStream(byte[] buffer, bool writable = false)
        : base(buffer, writable)
    {
    }

    public long ReadCalls { get; private set; }
    public long WriteCalls { get; private set; }
    public long SeekCalls { get; private set; }
    public long SetLengthCalls { get; private set; }
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }
    public long GrowthEvents { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;
        var read = base.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        var read = base.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin loc)
    {
        SeekCalls++;
        return base.Seek(offset, loc);
    }

    public override void SetLength(long value)
    {
        SetLengthCalls++;
        if (value > Capacity) GrowthEvents++;
        base.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CountWrite(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CountWrite(buffer.Length);
        base.Write(buffer);
    }

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

internal readonly record struct StreamDiagnostics(
    long WriteCalls,
    long ReadCalls,
    long SeekCalls,
    long SetLengthCalls,
    long BytesWritten,
    long GrowthEvents);

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

/// <summary>
/// Writer 非同步取消、失敗層疊與狀態鎖定測試套件。
/// </summary>
public sealed class WriterAsyncCancellationTests
{
    [Fact]
    public async Task MidAudioWriteCancellationFaultsWriter()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var gate = output.ArmWriteGate();
        using var cts = new CancellationTokenSource();
        var pending = writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero), cts.Token);

        cts.Cancel();
        gate.SetResult(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.FinalizeFileAsync());
    }

    [Fact]
    public async Task MidVideoFlushFailureFaultsWriter()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);

        await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        output.ArmThrowOnWrite();
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false)));

        Assert.Throws<InvalidOperationException>(() => writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x01 }, TimeSpan.FromMilliseconds(81), TimeSpan.FromMilliseconds(81))));
    }

    [Fact]
    public async Task MidFinalizeMdatBackpatchFailureFaultsWriter()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        output.ArmThrowOnWrite();
        await Assert.ThrowsAsync<IOException>(() => writer.FinalizeFileAsync());

        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20))));
    }

    [Fact]
    public async Task FastStartRelocationFailureFaultsWriter()
    {
        using var output = new AsyncOnlyGateStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart };
        using var writer = await Mp4Writer.CreateAsync(output, options);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        output.ArmThrowOnWrite();
        await Assert.ThrowsAsync<IOException>(() => writer.FinalizeFileAsync());

        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
    }

    [Fact]
    public async Task FastStartRelocationReadFailureFaultsWriter()
    {
        using var output = new AsyncOnlyGateStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart };
        using var writer = await Mp4Writer.CreateAsync(output, options);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        output.ThrowAfterByteOnRead(0);
        await Assert.ThrowsAsync<IOException>(() => writer.FinalizeFileAsync());
        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
    }

    [Fact]
    public async Task FragmentedMidStartupMetadataFailureFaultsWriter()
    {
        using var output = new AsyncOnlyGateStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented };
        using var writer = await Mp4Writer.CreateAsync(output, options);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);

        output.ArmThrowOnWrite();
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1))));

        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
    }

    [Fact]
    public async Task CancelledTokenOnFaultedWriterThrowsFaultedNotCancellation()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        output.ArmThrowOnWrite();
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)), cts.Token));
    }

    [Fact]
    public async Task CancelledTokenOnActiveWriterThrowsOverlapNotCancellation()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var gate = output.ArmWriteGate();
        var pending = writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)), cts.Token));

        gate.SetResult(true);
        await pending;
    }

    [Fact]
    public async Task CancelledTokenOnFinalizedWriterFinalizeSucceedsIdempotently()
    {
        using var output = new AsyncOnlyGateStream();
        using (var writer = await Mp4Writer.CreateAsync(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            await writer.FinalizeFileAsync();

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await writer.FinalizeFileAsync(cts.Token);
        }
    }

    [Fact]
    public async Task NullSampleTakesPrecedenceOverCancellationOnIdleWriter()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.WriteVideoNalUnitAsync(null!, cts.Token));

        await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        Assert.True(output.AsyncWriteCalls > 0);
    }

    [Fact]
    public async Task DisposedWriterThrowsObjectDisposedEvenWithCancelledToken()
    {
        using var output = new AsyncOnlyGateStream();
        var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        writer.Dispose();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero), cts.Token));
    }

    [Fact]
    public async Task FaultedWriterDisposeStillHonorsLeaveOpen()
    {
        var output = new LeaveOpenTrackingStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented };
        using (var writer = await Mp4Writer.CreateAsync(output, options, leaveOpen: false))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            output.ArmThrowOnWrite();
            await Assert.ThrowsAsync<IOException>(() =>
                writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1))));
            writer.Dispose();
        }

        Assert.True(output.Closed);
    }

    [Fact]
    public async Task PreCancelledFinalizeFileAsyncKeepsWriterUsable()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => writer.FinalizeFileAsync(cts.Token));

        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)));
        await writer.FinalizeFileAsync();
        Assert.True(output.AsyncWriteCalls > 1);
    }

    [Fact]
    public async Task DisposeDuringActiveAsyncOperationThrowsAndDoesNotCloseStream()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var gate = output.ArmWriteGate();
        var pending = writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        Assert.Throws<InvalidOperationException>(() => writer.Dispose());
        Assert.False(output.IsClosed);

        gate.SetResult(true);
        await pending;
    }

    [Fact]
    public async Task ConcurrentSyncCallersDuringPendingAsyncAreAllRejectedByAtomicGate()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var gate = output.ArmWriteGate();
        var pending = writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<InvalidOperationException>();
        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)));
                }
                catch (InvalidOperationException ex)
                {
                    exceptions.Enqueue(ex);
                }
            });
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();
        Assert.Equal(threadCount, exceptions.Count);

        gate.SetResult(true);
        await pending;
    }

    [Fact]
    public async Task ArbitraryExceptionAfterOutputRiskBoundaryFaultsWriter()
    {
        var output = new CustomThrowAsyncStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        output.ArmWriteException(new InvalidOperationException("injected non-IOException failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero)));

        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20))));
    }

    private sealed class LeaveOpenTrackingStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();
        private bool _throwOnWrite;
        public bool Closed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public void ArmThrowOnWrite() => _throwOnWrite = true;
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _inner.Read(buffer, offset, count);
            return Task.FromResult(count);
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_throwOnWrite)
            {
                _throwOnWrite = false;
                throw new IOException("injected");
            }
            _inner.Write(buffer, offset, count);
            await Task.CompletedTask;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Closed = true;
            base.Dispose(disposing);
            _inner.Dispose();
        }
    }

    private sealed class CustomThrowAsyncStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();
        private Exception? _writeException;
        public void ArmWriteException(Exception ex) => _writeException = ex;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("CustomThrowAsyncStream rejects synchronous reads.");
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("CustomThrowAsyncStream rejects synchronous writes.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _inner.Read(buffer, offset, count);
            return Task.FromResult(count);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var ex = Interlocked.Exchange(ref _writeException, null);
            if (ex != null) return Task.FromException(ex);
            _inner.Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
    }
}
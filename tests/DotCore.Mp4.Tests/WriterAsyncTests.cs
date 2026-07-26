using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class WriterAsyncTests
{
    [Fact]
    public async Task CreateAsyncProgressiveWritesHeaderViaAsyncWritesOnly()
    {
        using var output = new AsyncOnlyGateStream();
        using (var writer = await Mp4Writer.CreateAsync(output))
        {
            Assert.Equal(0, output.SyncWriteCalls);
            Assert.True(output.AsyncWriteCalls > 0);
            Assert.True(output.InnerLength > 0);
            Assert.Equal(Mp4WriteMode.Progressive, ExtractMode(writer));
        }

        Assert.False(output.IsClosed);
    }

    [Fact]
    public async Task CreateAsyncFragmentedEmitsZeroOutput()
    {
        using var output = new AsyncOnlyGateStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented };
        using (var writer = await Mp4Writer.CreateAsync(output, options))
        {
            Assert.Equal(0, output.AsyncWriteCalls);
            Assert.Equal(0, output.SyncWriteCalls);
            Assert.Equal(0, output.InnerLength);
        }

        Assert.False(output.IsClosed);
    }

    [Fact]
    public async Task CreateAsyncRejectsNonWritableBeforeAnyHeaderAndDoesNotClose()
    {
        var output = new NonWritableTrackingStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Mp4Writer.CreateAsync(output, false));
        Assert.False(output.Closed);
    }

    [Fact]
    public async Task CreateAsyncMidHeaderCancelDoesNotReturnWriterAndDoesNotCloseCaller()
    {
        using var output = new AsyncOnlyGateStream();
        var gate = output.ArmWriteGate();
        using var cts = new CancellationTokenSource();
        var pending = Mp4Writer.CreateAsync(output, true, cts.Token);

        Assert.False(pending.IsCompleted);
        cts.Cancel();
        gate.SetResult(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.False(output.IsClosed);
    }

    [Fact]
    public async Task WriteAudioSampleAsyncUsesAsyncWritesAndRecordsSample()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        Assert.Equal(0, output.SyncWriteCalls);
        Assert.True(output.AsyncWriteCalls > 1);
    }

    [Fact]
    public async Task OverlappingSyncWriteWhileAsyncWritePendingIsRejectedAndFirstStillCompletes()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var gate = output.ArmWriteGate();
        var pending = writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20))));

        gate.SetResult(true);
        await pending;
        Assert.Equal(0, output.SyncWriteCalls);
    }

    [Fact]
    public async Task OverlappingAsyncWriteWhileAsyncWritePendingIsRejected()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        var gate = output.ArmWriteGate();
        var pending = writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20))));

        gate.SetResult(true);
        await pending;
    }

    [Fact]
    public async Task PreCancelledAsyncWriteCancelsAndKeepsWriterUsable()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero), cts.Token));

        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)));
        Assert.True(output.AsyncWriteCalls > 1);
    }

    [Fact]
    public async Task FaultedWriterRejectsAllSubsequentOperationsExceptDispose()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        output.ArmThrowOnWrite();
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero)));

        Assert.Throws<InvalidOperationException>(() =>
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x23, 0x10 }, TimeSpan.FromMilliseconds(40))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.FinalizeFileAsync());
        Assert.Throws<InvalidOperationException>(() => writer.FinalizeFile());
    }

    [Fact]
    public async Task PreOutputValidationFailureKeepsWriterUsable()
    {
        using var output = new AsyncOnlyGateStream();
        using var writer = await Mp4Writer.CreateAsync(output);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            writer.WriteAudioSampleAsync(null!));

        await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
        Assert.True(output.AsyncWriteCalls > 1);
    }

    [Fact]
    public async Task FinalizedWriterAsyncFinalizeIsIdempotent()
    {
        using var output = new AsyncOnlyGateStream();
        using (var writer = await Mp4Writer.CreateAsync(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            await writer.FinalizeFileAsync();

            var length = output.InnerLength;
            await writer.FinalizeFileAsync();
            Assert.Equal(length, output.InnerLength);
        }
    }

    [Fact]
    public async Task FinalizeFileAsyncWithAlreadyCancelledTokenStillCompletesWhenFinalized()
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
    public async Task ProgressiveAsyncOutputIsByteIdenticalToSyncOutput()
    {
        var syncBytes = BuildSyncProgressive();
        var asyncBytes = await BuildAsyncProgressive();
        Assert.Equal(Sha256(syncBytes), Sha256(asyncBytes));
    }

    [Fact]
    public async Task SequentialMixedSyncAsyncProducesIdenticalProgressiveOutput()
    {
        using var output = new MemoryStream();
        using (var writer = await Mp4Writer.CreateAsync(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)));
            await writer.FinalizeFileAsync();
        }

        var syncBytes = BuildSyncProgressiveTwoSamples();
        Assert.Equal(Sha256(syncBytes), Sha256(output.ToArray()));
    }

    [Fact]
    public async Task AsyncRoundTripThroughReaderMatchesSyncRoundTrip()
    {
        var asyncBytes = await BuildAsyncProgressive();
        using var readerStream = new MemoryStream(asyncBytes);
        using var reader = new Mp4Reader(readerStream);
        var samples = reader.ReadAudioSamples().Select(s => s.DataBytes.ToArray()).ToList();
        Assert.Single(samples);
        Assert.Equal(new byte[] { 0x21, 0x10 }, samples[0]);
    }

    [Fact]
    public async Task ProgressiveAsyncVideoAndAudioIsByteIdenticalAndRoundTrips()
    {
        var syncBytes = BuildSyncProgressiveVideoAudio();
        var asyncBytes = await BuildAsyncProgressiveVideoAudio();
        Assert.Equal(Sha256(syncBytes), Sha256(asyncBytes));

        using var reader = new Mp4Reader(new MemoryStream(asyncBytes));
        Assert.NotNull(reader.VideoConfiguration);
        Assert.NotNull(reader.AudioConfiguration);
        Assert.Equal(2, reader.ReadVideoNalUnits().Count());
        Assert.Single(reader.ReadAudioSamples());
    }

    [Fact]
    public async Task FastStartAsyncOutputIsByteIdenticalToSyncOutput()
    {
        var syncBytes = BuildSyncFastStart();
        var asyncBytes = await BuildAsyncFastStart();
        Assert.Equal(Sha256(syncBytes), Sha256(asyncBytes));
    }

    private static byte[] BuildSyncProgressive()
    {
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }
        return output.ToArray();
    }

    private static byte[] BuildSyncProgressiveTwoSamples()
    {
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x22, 0x10 }, TimeSpan.FromMilliseconds(20)));
            writer.FinalizeFile();
        }
        return output.ToArray();
    }

    private static async Task<byte[]> BuildAsyncProgressive()
    {
        using var output = new SeekableAsyncOnlyStream();
        using (var writer = await Mp4Writer.CreateAsync(output))
        {
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            await writer.FinalizeFileAsync();
        }
        return output.ToArray();
    }

    private static byte[] BuildSyncProgressiveVideoAudio()
    {
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }
        return output.ToArray();
    }

    private static async Task<byte[]> BuildAsyncProgressiveVideoAudio()
    {
        using var output = new SeekableAsyncOnlyStream();
        using (var writer = await Mp4Writer.CreateAsync(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            await writer.FinalizeFileAsync();
        }
        return output.ToArray();
    }

    [Fact]
    public async Task FragmentedAsyncOutputIsByteIdenticalToSyncAndRoundTrips()
    {
        var syncBytes = BuildSyncFragmented();
        var asyncBytes = await BuildAsyncFragmented();
        Assert.Equal(Sha256(syncBytes), Sha256(asyncBytes));

        using var reader = new Mp4Reader(new MemoryStream(asyncBytes));
        Assert.NotNull(reader.VideoConfiguration);
        Assert.NotNull(reader.AudioConfiguration);
        Assert.Equal(3, reader.ReadVideoNalUnits().Count());
        Assert.Single(reader.ReadAudioSamples());
    }

    [Fact]
    public async Task FragmentedAsyncOnNonSeekableStreamMatchesSyncOutput()
    {
        var syncBytes = BuildSyncFragmented();
        using var output = new NonSeekableAsyncWriteStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented };
        using (var writer = await Mp4Writer.CreateAsync(output, options))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            await writer.WriteAudioSampleAsync(new EncodedAudioSample(new byte[] { 0x21, 0x10 }, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20)));
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x03 }, TimeSpan.FromMilliseconds(81), TimeSpan.FromMilliseconds(81)));
            await writer.FinalizeFileAsync();
        }

        Assert.Equal(0, output.SyncWriteCalls);
        Assert.True(output.AsyncWriteCalls > 0);
        Assert.Equal(Sha256(syncBytes), Sha256(output.ToArray()));
    }

    private static byte[] BuildSyncFragmented()
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented };
        using (var writer = new Mp4Writer(output, options))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            writer.WriteAudioSample(new EncodedAudioSample(new byte[] { 0x21, 0x10 }, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20)));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x03 }, TimeSpan.FromMilliseconds(81), TimeSpan.FromMilliseconds(81)));
            writer.FinalizeFile();
        }
        return output.ToArray();
    }

    private static async Task<byte[]> BuildAsyncFragmented()
    {
        using var output = new SeekableAsyncOnlyStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented };
        using (var writer = await Mp4Writer.CreateAsync(output, options))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            await writer.WriteAudioSampleAsync(new EncodedAudioSample(new byte[] { 0x21, 0x10 }, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20)));
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x03 }, TimeSpan.FromMilliseconds(81), TimeSpan.FromMilliseconds(81)));
            await writer.FinalizeFileAsync();
        }
        return output.ToArray();
    }

    private static byte[] BuildSyncFastStart()
    {
        using var output = new MemoryStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart };
        using (var writer = new Mp4Writer(output, options))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }
        return output.ToArray();
    }

    private static async Task<byte[]> BuildAsyncFastStart()
    {
        using var output = new SeekableAsyncOnlyStream();
        var options = new Mp4WriterOptions { Mode = Mp4WriteMode.FastStart };
        using (var writer = await Mp4Writer.CreateAsync(output, options))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
            await writer.WriteVideoNalUnitAsync(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(41), TimeSpan.FromMilliseconds(41), false));
            await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            await writer.FinalizeFileAsync();
        }
        return output.ToArray();
    }

    private static Mp4WriteMode ExtractMode(Mp4Writer writer)
    {
        var field = typeof(Mp4Writer).GetField("_mode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (Mp4WriteMode)field!.GetValue(writer)!;
    }

    private static string Sha256(byte[] data)
    {
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(data));
    }

    private sealed class NonWritableTrackingStream : MemoryStream
    {
        public NonWritableTrackingStream() : base(new byte[0], false) { }
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }
}
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

/// <summary>
/// 非同步 API 契約與公用介面結構檢視測試套件。
/// </summary>
public sealed class AsyncApiContractTests
{
    [Fact]
    public void ReaderExposesCreateAsyncFactoryWithApprovedSignature()
    {
        var method = typeof(Mp4Reader).GetMethod(
            "CreateAsync",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Stream), typeof(bool), typeof(CancellationToken) },
            null);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<Mp4Reader>), method!.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(Stream), parameters[0].ParameterType);
        Assert.Equal(typeof(bool), parameters[1].ParameterType);
        Assert.True(parameters[1].HasDefaultValue);
        Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);
        Assert.True(parameters[2].HasDefaultValue);
    }

    [Fact]
    public void WriterExposesTwoCreateAsyncOverloadsWithApprovedSignatures()
    {
        var leaveOpenFactory = typeof(Mp4Writer).GetMethod(
            "CreateAsync",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Stream), typeof(bool), typeof(CancellationToken) },
            null);

        Assert.NotNull(leaveOpenFactory);
        Assert.Equal(typeof(Task<Mp4Writer>), leaveOpenFactory!.ReturnType);
        Assert.True(leaveOpenFactory.GetParameters()[1].HasDefaultValue);
        Assert.True(leaveOpenFactory.GetParameters()[2].HasDefaultValue);

        var optionsFactory = typeof(Mp4Writer).GetMethod(
            "CreateAsync",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Stream), typeof(Mp4WriterOptions), typeof(bool), typeof(CancellationToken) },
            null);

        Assert.NotNull(optionsFactory);
        Assert.Equal(typeof(Task<Mp4Writer>), optionsFactory!.ReturnType);
        Assert.Equal(typeof(Mp4WriterOptions), optionsFactory!.GetParameters()[1].ParameterType);
        Assert.True(optionsFactory.GetParameters()[2].HasDefaultValue);
        Assert.True(optionsFactory.GetParameters()[3].HasDefaultValue);
    }

    [Fact]
    public void WriterExposesApprovedAsyncInstanceMembersWithTrailingCancellationToken()
    {
        var writeVideo = typeof(Mp4Writer).GetMethod(
            "WriteVideoNalUnitAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(EncodedVideoNalUnit), typeof(CancellationToken) },
            null);

        Assert.NotNull(writeVideo);
        Assert.Equal(typeof(Task), writeVideo!.ReturnType);
        Assert.True(writeVideo.GetParameters()[1].HasDefaultValue);

        var writeAudio = typeof(Mp4Writer).GetMethod(
            "WriteAudioSampleAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(EncodedAudioSample), typeof(CancellationToken) },
            null);

        Assert.NotNull(writeAudio);
        Assert.Equal(typeof(Task), writeAudio!.ReturnType);
        Assert.True(writeAudio.GetParameters()[1].HasDefaultValue);

        var finalize = typeof(Mp4Writer).GetMethod(
            "FinalizeFileAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(CancellationToken) },
            null);

        Assert.NotNull(finalize);
        Assert.Equal(typeof(Task), finalize!.ReturnType);
        Assert.True(finalize.GetParameters()[0].HasDefaultValue);
    }

    [Fact]
    public void AsyncMembersAreNotExposedAsAliasesOrValueTaskOrMemoryOverloads()
    {
        Assert.Null(typeof(Mp4Writer).GetMethod("WriteVideoAsync"));
        Assert.Null(typeof(Mp4Writer).GetMethod("WriteAacSampleAsync"));
        Assert.Null(typeof(Mp4Writer).GetMethod("CompleteAsync"));
        Assert.Null(typeof(Mp4Writer).GetMethod("FinishAsync"));

        foreach (var method in typeof(Mp4Writer).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (!method.Name.EndsWith("Async", StringComparison.Ordinal)) continue;
            Assert.False(method.ReturnType.Name.Contains("ValueTask", StringComparison.Ordinal),
                method.Name + " must not return ValueTask.");
            foreach (var p in method.GetParameters())
            {
                Assert.False(p.ParameterType.Name.Contains("Memory`1", StringComparison.Ordinal),
                    method.Name + " must not accept Memory<byte>.");
            }
        }
    }

    [Fact]
    public async Task ReaderCreateAsyncUsesAsyncSnapshotAndReturnsReader()
    {
        var mp4 = BuildMinimalMp4();
        using var input = new AsyncOnlyGateStream(mp4);
        var read = await Mp4Reader.CreateAsync(input);
        using (read)
        {
            Assert.NotNull(read.AudioConfiguration);
        }

        Assert.Equal(0, input.SyncReadCalls);
        Assert.True(input.AsyncReadCalls > 0);
    }

    [Fact]
    public async Task ReaderCreateAsyncRestoresNonzeroPosition()
    {
        var mp4 = BuildMinimalMp4();
        using var input = new AsyncOnlyGateStream(mp4);
        input.Position = 5;

        using (await Mp4Reader.CreateAsync(input))
        {
        }

        Assert.Equal(5, input.Position);
    }

    [Fact]
    public async Task ReaderCreateAsyncPreCancelDoesNotReadStream()
    {
        var mp4 = BuildMinimalMp4();
        using var input = new AsyncOnlyGateStream(mp4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Mp4Reader.CreateAsync(input, true, cts.Token));
        Assert.Equal(0, input.AsyncReadCalls);
        Assert.False(input.IsClosed);
    }

    [Fact]
    public async Task ReaderCreateAsyncStaysPendingUntilDelayedReadsComplete()
    {
        var mp4 = BuildMinimalMp4();
        using var input = new AsyncOnlyGateStream(mp4);
        var gate = input.ArmReadGate();

        var pending = Mp4Reader.CreateAsync(input);
        Assert.False(pending.IsCompleted);
        gate.SetResult(true);
        using (await pending)
        {
            Assert.True(input.AsyncReadCalls > 0);
        }

        Assert.Equal(0, input.SyncReadCalls);
    }

    [Fact]
    public async Task ReaderCreateAsyncMidReadCancelRestoresPositionAndDoesNotReturnReader()
    {
        var mp4 = BuildMinimalMp4();
        using var input = new AsyncOnlyGateStream(mp4);
        input.Position = 7;
        var gate = input.ArmReadGate();
        using var cts = new CancellationTokenSource();

        var pending = Mp4Reader.CreateAsync(input, true, cts.Token);
        cts.Cancel();
        gate.SetResult(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.False(input.IsClosed);
    }

    [Fact]
    public async Task ReaderCreateAsyncFactoryFailureDoesNotCloseCallerStreamWhenLeaveOpenFalse()
    {
        var mp4 = BuildMinimalMp4();
        using var input = new AsyncOnlyGateStream(mp4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Mp4Reader.CreateAsync(input, false, cts.Token));
        Assert.False(input.IsClosed);
    }

    private static byte[] BuildMinimalMp4()
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
}
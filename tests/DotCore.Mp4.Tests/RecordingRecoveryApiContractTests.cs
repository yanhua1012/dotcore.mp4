using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// 可復原錄影公開 API 的編譯期與相容性契約測試。
/// </summary>
public sealed class RecordingRecoveryApiContractTests
{
    [Fact]
    public void RecordingRecoverySurfaceExposesPathBasedFactoriesOptionsAndResult()
    {
        Func<string, Mp4WriterOptions?, Mp4RecordingWriter> create =
            (targetPath, options) => new Mp4RecordingWriter(targetPath, options);
        Func<string, Mp4RecordingRecoveryOptions?, Mp4RecordingRecoveryResult> recover =
            (targetPath, options) => Mp4RecordingRecovery.Recover(targetPath, options);
        Func<string, Mp4RecordingRecoveryOptions?, CancellationToken, Task<Mp4RecordingRecoveryResult>> recoverAsync =
            (targetPath, options, cancellationToken) => Mp4RecordingRecovery.RecoverAsync(targetPath, options, cancellationToken);

        Assert.NotNull(create);
        Assert.NotNull(recover);
        Assert.NotNull(recoverAsync);
        Assert.Equal(Mp4WriteMode.Progressive, new Mp4WriterOptions().Mode);
        Assert.False(new Mp4RecordingRecoveryOptions().EnableHeuristicRecovery);
        Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, default(Mp4RecordingRecoveryResult).Tier);
    }

    [Fact]
    public void LegacyStreamWriterDoesNotCreateRecordingArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcore-mp4-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var filesBefore = Directory.GetFiles(directory);

            using (var output = new MemoryStream())
            using (var writer = new Mp4Writer(output))
            {
                writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
                writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
                writer.FinalizeFile();
            }

            Assert.Equal(filesBefore, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InterruptedRecordingKeepsJournalAndCaptureUntilExactRecoveryDeliversTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcore-mp4-recovery-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(directory, "recording.mp4");
        Directory.CreateDirectory(directory);
        try
        {
            string journalPath;
            string capturePath;
            using (var writer = new Mp4RecordingWriter(targetPath))
            {
                journalPath = writer.JournalPath;
                capturePath = writer.CapturePath;
                writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
                writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            }

            Assert.True(File.Exists(journalPath));
            Assert.True(File.Exists(capturePath));
            Assert.False(File.Exists(targetPath));

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.True(
                result.Tier == Mp4RecordingRecoveryTier.Exact,
                string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>()));
            Assert.Equal(targetPath, result.TargetPath);
            Assert.True(File.Exists(targetPath));
            Assert.False(File.Exists(journalPath));
            Assert.False(File.Exists(capturePath));

            var samples = new List<byte[]>();
            using (var input = File.OpenRead(targetPath))
            using (var reader = new Mp4Reader(input))
            {
                reader.AacSampleRead += (_, sample) => samples.Add(sample.Data);
                reader.Read();
            }

            Assert.Equal(new[] { new byte[] { 0x21, 0x10 } }, samples);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(Mp4WriteMode.Progressive, VideoCodec.H264)]
    [InlineData(Mp4WriteMode.FastStart, VideoCodec.H264)]
    [InlineData(Mp4WriteMode.Fragmented, VideoCodec.H264)]
    [InlineData(Mp4WriteMode.Progressive, VideoCodec.H265)]
    [InlineData(Mp4WriteMode.FastStart, VideoCodec.H265)]
    [InlineData(Mp4WriteMode.Fragmented, VideoCodec.H265)]
    public void InterruptedRecordingRecoversJournalConfirmedCodecAndLayout(
        Mp4WriteMode mode,
        VideoCodec codec)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcore-mp4-recovery-matrix-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(directory, "recording.mp4");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = codec == VideoCodec.H264 ? TestMedia.H264Configuration : TestMedia.H265Configuration;
            var firstNal = codec == VideoCodec.H264 ? new byte[] { 0x65, 0x01 } : new byte[] { 0x26, 0x01 };
            var secondNal = codec == VideoCodec.H264 ? new byte[] { 0x41, 0x02 } : new byte[] { 0x02, 0x01 };
            using (var writer = new Mp4RecordingWriter(targetPath, new Mp4WriterOptions { Mode = mode }))
            {
                writer.SetVideoCodecConfiguration(configuration);
                writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
                writer.WriteVideoNalUnit(TestMedia.Video(firstNal, TimeSpan.Zero, TimeSpan.Zero, isKeyFrame: true));
                writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
                writer.WriteVideoNalUnit(TestMedia.Video(secondNal, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), isKeyFrame: false));
            }

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.True(
                result.Tier == Mp4RecordingRecoveryTier.Exact,
                string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>()));
            using (var input = File.OpenRead(targetPath))
            using (var reader = new Mp4Reader(input))
            {
                Assert.Equal(codec, reader.VideoConfiguration!.Codec);
                Assert.NotNull(reader.AudioConfiguration);
                var videoPayloads = new List<byte[]>();
                var audioPayloads = new List<byte[]>();
                reader.VideoNalUnitRead += (_, sample) => videoPayloads.Add(sample.Data);
                reader.AacSampleRead += (_, sample) => audioPayloads.Add(sample.Data);
                reader.Read();

                Assert.Equal(new[] { firstNal, secondNal }, videoPayloads);
                Assert.Equal(new[] { new byte[] { 0x21, 0x10 } }, audioPayloads);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(Mp4WriteMode.Progressive, VideoCodec.H264)]
    [InlineData(Mp4WriteMode.FastStart, VideoCodec.H264)]
    [InlineData(Mp4WriteMode.Fragmented, VideoCodec.H264)]
    [InlineData(Mp4WriteMode.Progressive, VideoCodec.H265)]
    [InlineData(Mp4WriteMode.FastStart, VideoCodec.H265)]
    [InlineData(Mp4WriteMode.Fragmented, VideoCodec.H265)]
    public async Task InterruptedAsyncRecordingRecoversJournalConfirmedCodecAndLayout(
        Mp4WriteMode mode,
        VideoCodec codec)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcore-mp4-recovery-async-matrix-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(directory, "recording.mp4");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = codec == VideoCodec.H264 ? TestMedia.H264Configuration : TestMedia.H265Configuration;
            var firstNal = codec == VideoCodec.H264 ? new byte[] { 0x65, 0x01 } : new byte[] { 0x26, 0x01 };
            var secondNal = codec == VideoCodec.H264 ? new byte[] { 0x41, 0x02 } : new byte[] { 0x02, 0x01 };
            using (var writer = await Mp4RecordingWriter.CreateAsync(
                targetPath,
                new Mp4WriterOptions { Mode = mode }))
            {
                await writer.SetVideoCodecConfigurationAsync(configuration);
                await writer.SetAudioCodecConfigurationAsync(TestMedia.AacConfiguration);
                await writer.WriteVideoNalUnitAsync(TestMedia.Video(firstNal, TimeSpan.Zero, TimeSpan.Zero, isKeyFrame: true));
                await writer.WriteAudioSampleAsync(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
                await writer.WriteVideoNalUnitAsync(
                    TestMedia.Video(secondNal, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), isKeyFrame: false));
            }

            var result = await Mp4RecordingRecovery.RecoverAsync(targetPath);

            Assert.True(
                result.Tier == Mp4RecordingRecoveryTier.Exact,
                string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>()));
            using (var input = File.OpenRead(targetPath))
            using (var reader = new Mp4Reader(input))
            {
                Assert.Equal(codec, reader.VideoConfiguration!.Codec);
                Assert.NotNull(reader.AudioConfiguration);
                var videoPayloads = new List<byte[]>();
                var audioPayloads = new List<byte[]>();
                reader.VideoNalUnitRead += (_, sample) => videoPayloads.Add(sample.Data);
                reader.AacSampleRead += (_, sample) => audioPayloads.Add(sample.Data);
                reader.Read();

                Assert.Equal(new[] { firstNal, secondNal }, videoPayloads);
                Assert.Equal(new[] { new byte[] { 0x21, 0x10 } }, audioPayloads);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingJournalRecoversOnlyCompleteFragmentedPrefixStructurally()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcore-mp4-structural-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-fixture";
        Directory.CreateDirectory(directory);
        try
        {
            using (var output = new FileStream(capturePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new Mp4Writer(
                output,
                new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented },
                leaveOpen: true))
            {
                writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
                writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.Zero, TimeSpan.Zero, isKeyFrame: true));
                writer.WriteVideoNalUnit(TestMedia.Video(
                    new byte[] { 0x41, 0x02 },
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(40),
                    isKeyFrame: false));
                writer.FinalizeFile();
            }

            File.AppendAllBytes(capturePath, new byte[] { 0, 0, 0, 16, (byte)'m', (byte)'o' });

            using (var truncatedInput = File.OpenRead(capturePath))
            {
                Assert.Throws<Mp4FormatException>(() => new Mp4Reader(truncatedInput));
            }

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Structural, result.Tier);
            Assert.True(result.Warnings!.Count > 0);
            using (var input = File.OpenRead(targetPath))
            using (var reader = new Mp4Reader(input))
            {
                var payloads = new List<byte[]>();
                reader.VideoNalUnitRead += (_, sample) => payloads.Add(sample.Data);
                reader.Read();
                Assert.Equal(
                    new[] { new byte[] { 0x65, 0x01 }, new byte[] { 0x41, 0x02 } },
                    payloads);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptJournalDoesNotDeliverOrReplaceTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dotcore-mp4-corrupt-journal-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(directory, "recording.mp4");
        Directory.CreateDirectory(directory);
        try
        {
            string journalPath;
            string capturePath;
            using (var writer = new Mp4RecordingWriter(targetPath))
            {
                journalPath = writer.JournalPath;
                capturePath = writer.CapturePath;
                writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
                writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            }

            var journal = File.ReadAllBytes(journalPath);
            journal[journal.Length - 1] ^= 0xff;
            File.WriteAllBytes(journalPath, journal);

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, result.Tier);
            Assert.False(File.Exists(targetPath));
            Assert.True(File.Exists(journalPath));
            Assert.True(File.Exists(capturePath));
            Assert.Contains(result.Warnings!, warning => warning.IndexOf("checksum", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// 可復原錄影之截斷、sidecar 與生命週期邊界測試。
/// </summary>
public sealed class RecordingRecoveryEdgeTests
{
    [Theory]
    [InlineData("moof-header")]
    [InlineData("mdat-header")]
    [InlineData("mdat-payload")]
    [InlineData("semantic")]
    public void TruncatedOrInvalidFinalFragmentIsStrictlyRejectedAndStructuralRecoveryKeepsValidPrefix(string damage)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-" + damage;
        try
        {
            var complete = CreateFragmentedVideoFile();
            var damaged = DamageLastFragment(complete, damage);
            File.WriteAllBytes(capturePath, damaged);

            AssertStrictReaderRejects(capturePath);

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Structural, result.Tier);
            Assert.True(File.Exists(capturePath));
            Assert.Equal(damaged, File.ReadAllBytes(capturePath));
            Assert.True(File.Exists(targetPath));
            AssertValidVideoPrefix(targetPath, maximumSamples: 2);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("short-capture")]
    public void UnusableJournalOrCapturePreservesRecoveryInputsAndTarget(string failure)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        try
        {
            using (var writer = CreateInterruptedAudioRecording(targetPath))
            {
            }

            var journalPath = targetPath + ".dotcore-journal";
            var capturePath = Directory.GetFiles(directory, "recording.mp4.dotcore-capture-*").Single();
            var captureBefore = File.ReadAllBytes(capturePath);
            switch (failure)
            {
                case "checksum":
                    var checksumJournal = File.ReadAllBytes(journalPath);
                    checksumJournal[checksumJournal.Length - 1] ^= 0xff;
                    File.WriteAllBytes(journalPath, checksumJournal);
                    break;
                case "version":
                    var versionJournal = File.ReadAllBytes(journalPath);
                    versionJournal[8] = 0xff;
                    File.WriteAllBytes(journalPath, versionJournal);
                    break;
                case "identity":
                    var identityJournal = File.ReadAllBytes(journalPath);
                    identityJournal[10] ^= 0xff;
                    File.WriteAllBytes(journalPath, identityJournal);
                    break;
                case "empty":
                    File.WriteAllBytes(journalPath, Array.Empty<byte>());
                    break;
                case "missing":
                    File.Delete(journalPath);
                    break;
                case "short-capture":
                    using (var capture = new FileStream(capturePath, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        capture.SetLength(67);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, result.Tier);
            Assert.False(File.Exists(targetPath));
            Assert.True(File.Exists(capturePath));
            if (failure == "short-capture")
            {
                Assert.Equal(67, new FileInfo(capturePath).Length);
            }
            else
            {
                Assert.Equal(captureBefore, File.ReadAllBytes(capturePath));
            }

            Assert.Equal(failure != "missing", File.Exists(journalPath));
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Fact]
    public void ActiveWriterExcludesRecoveryUntilDisposedThenAllowsCleanRetry()
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        try
        {
            using (var writer = CreateInterruptedAudioRecording(targetPath))
            {
                var blocked = Mp4RecordingRecovery.Recover(targetPath);

                Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, blocked.Tier);
                Assert.False(File.Exists(targetPath));
                Assert.True(File.Exists(writer.JournalPath));
                Assert.True(File.Exists(writer.CapturePath));
            }

            var recovered = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Exact, recovered.Tier);
            Assert.True(File.Exists(targetPath));
            Assert.Empty(Directory.GetFiles(directory, "recording.mp4.dotcore-*"));
            AssertValidAudio(targetPath);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Fact]
    public void ExistingUnsafeTargetIsNeverReplacedAndRecoveryInputsRemain()
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var protectedTarget = new byte[] { 0x70, 0x72, 0x6f, 0x74, 0x65, 0x63, 0x74 };
        try
        {
            string journalPath;
            string capturePath;
            using (var writer = CreateInterruptedAudioRecording(targetPath))
            {
                journalPath = writer.JournalPath;
                capturePath = writer.CapturePath;
            }

            var captureBefore = File.ReadAllBytes(capturePath);
            File.WriteAllBytes(targetPath, protectedTarget);

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, result.Tier);
            Assert.Equal(protectedTarget, File.ReadAllBytes(targetPath));
            Assert.True(File.Exists(journalPath));
            Assert.Equal(captureBefore, File.ReadAllBytes(capturePath));
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Fact]
    public void CompletedStaleJournalCanBeRetriedWithoutChangingDeliveredTarget()
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        try
        {
            string journalPath;
            using (var writer = CreateInterruptedAudioRecording(targetPath))
            {
                journalPath = writer.JournalPath;
            }

            var journalBeforeCompletion = File.ReadAllBytes(journalPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Exact, Mp4RecordingRecovery.Recover(targetPath).Tier);
            var delivered = File.ReadAllBytes(targetPath);
            var staleJournal = AppendCompletedRecord(journalBeforeCompletion, HashFile(targetPath));

            File.WriteAllBytes(targetPath + ".dotcore-journal", staleJournal);
            var firstRetry = Mp4RecordingRecovery.Recover(targetPath);
            Assert.Equal(Mp4RecordingRecoveryTier.Exact, firstRetry.Tier);
            Assert.Equal(delivered, File.ReadAllBytes(targetPath));
            Assert.False(File.Exists(targetPath + ".dotcore-journal"));

            File.WriteAllBytes(targetPath + ".dotcore-journal", staleJournal);
            var secondRetry = Mp4RecordingRecovery.Recover(targetPath);
            Assert.Equal(Mp4RecordingRecoveryTier.Exact, secondRetry.Tier);
            Assert.Equal(delivered, File.ReadAllBytes(targetPath));
            Assert.False(File.Exists(targetPath + ".dotcore-journal"));
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoJournalHeuristicRecoversH264OrH265VideoOnly(bool h265)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-heuristic";
        var configuration = h265 ? TestMedia.H265Configuration : TestMedia.H264Configuration;
        var frame = h265 ? new byte[] { 0x26, 0x01 } : new byte[] { 0x65, 0x01 };
        try
        {
            WriteHeuristicCapture(capturePath, configuration, frame);

            var result = Mp4RecordingRecovery.Recover(
                targetPath,
                new Mp4RecordingRecoveryOptions { EnableHeuristicRecovery = true });

            Assert.Equal(Mp4RecordingRecoveryTier.Heuristic, result.Tier);
            Assert.Contains(result.Warnings!, warning => warning.IndexOf("video-only", StringComparison.OrdinalIgnoreCase) >= 0);
            using (var input = File.OpenRead(targetPath))
            using (var reader = new Mp4Reader(input))
            {
                Assert.Equal(configuration.Codec, reader.VideoConfiguration!.Codec);
                Assert.Null(reader.AudioConfiguration);
                Assert.Equal(new[] { frame }, reader.ReadVideoNalUnits().Select(sample => sample.Data));
                Assert.Empty(reader.ReadAudioSamples());
            }
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Fact]
    public void NoJournalHeuristicWithoutParameterSetsDoesNotCreateTargetOrConsumeCapture()
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-incomplete-heuristic";
        try
        {
            WriteOpenEndedMdat(capturePath, new[] { new byte[] { 0x65, 0x01 } });
            var captureBefore = File.ReadAllBytes(capturePath);

            var result = Mp4RecordingRecovery.Recover(
                targetPath,
                new Mp4RecordingRecoveryOptions { EnableHeuristicRecovery = true });

            Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, result.Tier);
            Assert.False(File.Exists(targetPath));
            Assert.Equal(captureBefore, File.ReadAllBytes(capturePath));
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    private static Mp4RecordingWriter CreateInterruptedAudioRecording(string targetPath)
    {
        var writer = new Mp4RecordingWriter(targetPath);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
        return writer;
    }

    private static byte[] CreateFragmentedVideoFile()
    {
        using (var output = new MemoryStream())
        {
            using (var writer = new Mp4Writer(
                output,
                new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented },
                leaveOpen: true))
            {
                writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
                writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.Zero, TimeSpan.Zero));
                writer.WriteVideoNalUnit(TestMedia.Video(
                    new byte[] { 0x65, 0x02 },
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.FromMilliseconds(40)));
                writer.WriteVideoNalUnit(TestMedia.Video(
                    new byte[] { 0x41, 0x03 },
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(80),
                    isKeyFrame: false));
                writer.FinalizeFile();
            }

            return output.ToArray();
        }
    }

    private static byte[] DamageLastFragment(byte[] source, string damage)
    {
        var boxes = ReadTopLevelBoxes(source);
        var moofs = boxes.Where(box => box.Type == "moof").ToArray();
        var mdats = boxes.Where(box => box.Type == "mdat").ToArray();
        Assert.True(moofs.Length >= 2);
        Assert.Equal(moofs.Length, mdats.Length);
        var lastMoof = moofs[moofs.Length - 1];
        var lastMdat = mdats[mdats.Length - 1];
        switch (damage)
        {
            case "moof-header":
                return source.Take(lastMoof.Start + 6).ToArray();
            case "mdat-header":
                return source.Take(lastMdat.Start + 6).ToArray();
            case "mdat-payload":
                return source.Take(lastMdat.PayloadStart + 1).ToArray();
            case "semantic":
                var invalid = source.ToArray();
                var tfhd = FindBoxType(invalid, "tfhd", lastMoof.Start, lastMoof.End);
                Assert.True(tfhd >= 0);
                WriteUInt32(invalid, tfhd + 12, 99);
                return invalid;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }
    }

    private static void AssertStrictReaderRejects(string path)
    {
        using (var input = File.OpenRead(path))
        {
            Assert.Throws<Mp4FormatException>(() => new Mp4Reader(input));
        }
    }

    private static void AssertValidVideoPrefix(string path, int maximumSamples)
    {
        using (var input = File.OpenRead(path))
        using (var reader = new Mp4Reader(input))
        {
            var samples = reader.ReadVideoNalUnits().ToArray();
            Assert.InRange(samples.Length, 1, maximumSamples);
            Assert.Equal(new byte[] { 0x65, 0x01 }, samples[0].Data);
        }
    }

    private static void AssertValidAudio(string path)
    {
        using (var input = File.OpenRead(path))
        using (var reader = new Mp4Reader(input))
        {
            Assert.Equal(new[] { new byte[] { 0x21, 0x10 } }, reader.ReadAudioSamples().Select(sample => sample.Data));
        }
    }

    private static void WriteHeuristicCapture(
        string path,
        VideoCodecConfiguration configuration,
        byte[] frame)
    {
        var nals = new List<byte[]>();
        if (configuration.Vps != null && configuration.Vps.Length != 0) nals.Add(configuration.Vps);
        nals.Add(configuration.Sps);
        nals.Add(configuration.Pps);
        nals.Add(frame);
        WriteOpenEndedMdat(path, nals);
    }

    private static void WriteOpenEndedMdat(string path, IEnumerable<byte[]> nals)
    {
        using (var capture = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            capture.Write(new byte[]
            {
                0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0,
                (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2',
                0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t'
            });
            foreach (var nal in nals)
            {
                capture.WriteByte((byte)(nal.Length >> 24));
                capture.WriteByte((byte)(nal.Length >> 16));
                capture.WriteByte((byte)(nal.Length >> 8));
                capture.WriteByte((byte)nal.Length);
                capture.Write(nal, 0, nal.Length);
            }
        }
    }

    private static byte[] AppendCompletedRecord(byte[] journal, byte[] targetHash)
    {
        var record = new byte[41];
        record[0] = 4;
        WriteUInt32(record, 1, 32);
        Buffer.BlockCopy(targetHash, 0, record, 5, targetHash.Length);
        WriteUInt32(record, 37, Crc32(record, 0, 37));
        return journal.Concat(record).ToArray();
    }

    private static byte[] HashFile(string path)
    {
        using (var hash = SHA256.Create())
        using (var input = File.OpenRead(path))
        {
            return hash.ComputeHash(input);
        }
    }

    private static uint Crc32(byte[] data, int offset, int count)
    {
        var crc = 0xffffffffu;
        for (var index = 0; index < count; index++)
        {
            crc ^= data[offset + index];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static IReadOnlyList<Box> ReadTopLevelBoxes(byte[] data)
    {
        var boxes = new List<Box>();
        var offset = 0;
        while (offset < data.Length)
        {
            Assert.True(data.Length - offset >= 8);
            var size = checked((int)ReadUInt32(data, offset));
            Assert.InRange(size, 8, data.Length - offset);
            boxes.Add(new Box(ReadType(data, offset + 4), offset, size));
            offset += size;
        }

        return boxes;
    }

    private static int FindBoxType(byte[] data, string type, int start, int end)
    {
        for (var index = start; index <= end - 4; index++)
        {
            if (data[index] == type[0] &&
                data[index + 1] == type[1] &&
                data[index + 2] == type[2] &&
                data[index + 3] == type[3])
            {
                return index - 4;
            }
        }

        return -1;
    }

    private static string ReadType(byte[] data, int offset)
    {
        return new string(new[]
        {
            (char)data[offset],
            (char)data[offset + 1],
            (char)data[offset + 2],
            (char)data[offset + 3]
        });
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "recording-recovery-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private readonly struct Box
    {
        public Box(string type, int start, int size)
        {
            Type = type;
            Start = start;
            Size = size;
        }

        public string Type { get; }

        public int Start { get; }

        public int Size { get; }

        public int End => Start + Size;

        public int PayloadStart => Start + 8;
    }
}

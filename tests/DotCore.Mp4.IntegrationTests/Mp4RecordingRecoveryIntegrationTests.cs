using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DotCore.Mp4;
using Xunit;
using Xunit.Sdk;

namespace DotCore.Mp4.IntegrationTests;

/// <summary>
/// 驗證檔案導向錄影的中斷復原公開契約。
/// </summary>
public sealed class Mp4RecordingRecoveryIntegrationTests
{
    [Theory]
    [InlineData(false, Mp4WriteMode.Progressive, false)]
    [InlineData(false, Mp4WriteMode.FastStart, false)]
    [InlineData(false, Mp4WriteMode.Fragmented, false)]
    [InlineData(true, Mp4WriteMode.Progressive, false)]
    [InlineData(true, Mp4WriteMode.FastStart, false)]
    [InlineData(true, Mp4WriteMode.Fragmented, false)]
    [InlineData(false, Mp4WriteMode.Progressive, true)]
    [InlineData(false, Mp4WriteMode.FastStart, true)]
    [InlineData(false, Mp4WriteMode.Fragmented, true)]
    [InlineData(true, Mp4WriteMode.Progressive, true)]
    [InlineData(true, Mp4WriteMode.FastStart, true)]
    [InlineData(true, Mp4WriteMode.Fragmented, true)]
    public async Task InterruptedRecordingRecoversExactFileBackedRoundTrip(
        bool h265,
        Mp4WriteMode mode,
        bool asynchronous)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        try
        {
            if (asynchronous)
            {
                await WriteInterruptedRecordingAsync(targetPath, fixture, mode);
            }
            else
            {
                WriteInterruptedRecording(targetPath, fixture, mode);
            }

            Assert.False(File.Exists(targetPath));
            Assert.True(File.Exists(targetPath + ".dotcore-journal"));
            Assert.NotEmpty(Directory.GetFiles(directory, "recording.mp4.dotcore-capture-*"));

            var result = asynchronous
                ? await Mp4RecordingRecovery.RecoverAsync(targetPath)
                : Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Exact, result.Tier);
            Assert.Equal(targetPath, result.TargetPath);
            AssertRecoveredRoundTrip(targetPath, fixture);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false, Mp4WriteMode.Progressive, false, "h264")]
    [InlineData(false, Mp4WriteMode.FastStart, false, "h264")]
    [InlineData(false, Mp4WriteMode.Fragmented, false, "h264")]
    [InlineData(true, Mp4WriteMode.Progressive, false, "hevc")]
    [InlineData(true, Mp4WriteMode.FastStart, false, "hevc")]
    [InlineData(true, Mp4WriteMode.Fragmented, false, "hevc")]
    [InlineData(false, Mp4WriteMode.Progressive, true, "h264")]
    [InlineData(false, Mp4WriteMode.FastStart, true, "h264")]
    [InlineData(false, Mp4WriteMode.Fragmented, true, "h264")]
    [InlineData(true, Mp4WriteMode.Progressive, true, "hevc")]
    [InlineData(true, Mp4WriteMode.FastStart, true, "hevc")]
    [InlineData(true, Mp4WriteMode.Fragmented, true, "hevc")]
    public async Task RecoveredExactMatrixPassesFfprobeAndFfmpegValidation(
        bool h265,
        Mp4WriteMode mode,
        bool asynchronous,
        string expectedVideoCodec)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        try
        {
            if (asynchronous)
            {
                await WriteInterruptedRecordingAsync(targetPath, fixture, mode);
                var asyncResult = await Mp4RecordingRecovery.RecoverAsync(targetPath);
                Assert.Equal(Mp4RecordingRecoveryTier.Exact, asyncResult.Tier);
            }
            else
            {
                WriteInterruptedRecording(targetPath, fixture, mode);
                var result = Mp4RecordingRecovery.Recover(targetPath);
                Assert.Equal(Mp4RecordingRecoveryTier.Exact, result.Tier);
            }

            AssertExternalMediaTools(targetPath, expectedVideoCodec, expectAac: true);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingJournalRecoversCompleteFragmentedPrefixStructurally(bool h265)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-structural";
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        try
        {
            FixtureData.WriteMixedFile(capturePath, fixture, Mp4WriteMode.Fragmented);
            using (var capture = new FileStream(capturePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                capture.Write(new byte[] { 0, 0, 0, 16, (byte)'m', (byte)'o' }, 0, 6);
            }

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Structural, result.Tier);
            Assert.NotEmpty(result.Warnings!);
            AssertRecoveredRoundTrip(targetPath, fixture);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false, "h264")]
    [InlineData(true, "hevc")]
    public void StructuralRecoveryPassesFfprobeAndFfmpegValidation(bool h265, string expectedVideoCodec)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-structural";
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        try
        {
            FixtureData.WriteMixedFile(capturePath, fixture, Mp4WriteMode.Fragmented);
            using (var capture = new FileStream(capturePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                capture.Write(new byte[] { 0, 0, 0, 16, (byte)'m', (byte)'o' }, 0, 6);
            }

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.Structural, result.Tier);
            Assert.NotEmpty(result.Warnings!);
            AssertExternalMediaTools(targetPath, expectedVideoCodec, expectAac: true);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingJournalHeuristicRecoveryPreservesVideoOnlyH264OrH265(bool h265)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-heuristic";
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        try
        {
            WriteHeuristicCapture(capturePath, fixture);

            var result = Mp4RecordingRecovery.Recover(
                targetPath,
                new Mp4RecordingRecoveryOptions { EnableHeuristicRecovery = true });

            Assert.True(
                result.Tier == Mp4RecordingRecoveryTier.Heuristic,
                string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>()));
            Assert.Equal(targetPath, result.TargetPath);
            Assert.Contains(
                result.Warnings!,
                warning => warning.IndexOf("video-only", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.True(File.Exists(capturePath));
            AssertHeuristicRoundTrip(targetPath, fixture);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false, "h264")]
    [InlineData(true, "hevc")]
    public void HeuristicVideoOnlyRecoveryPassesFfprobeAndFfmpegValidation(
        bool h265,
        string expectedVideoCodec)
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-heuristic";
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        try
        {
            WriteHeuristicCapture(capturePath, fixture);

            var result = Mp4RecordingRecovery.Recover(
                targetPath,
                new Mp4RecordingRecoveryOptions { EnableHeuristicRecovery = true });

            Assert.True(
                result.Tier == Mp4RecordingRecoveryTier.Heuristic,
                string.Join(Environment.NewLine, result.Warnings ?? Array.Empty<string>()));
            Assert.Contains(
                result.Warnings!,
                warning => warning.IndexOf("video-only", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertExternalMediaTools(targetPath, expectedVideoCodec, expectAac: false);
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Fact]
    public void EmptyCaptureDoesNotCreateTargetAndPreservesRecoveryInput()
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-empty";
        try
        {
            File.WriteAllBytes(capturePath, Array.Empty<byte>());

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, result.Tier);
            Assert.Null(result.TargetPath);
            Assert.False(File.Exists(targetPath));
            Assert.True(File.Exists(capturePath));
            Assert.Empty(File.ReadAllBytes(capturePath));
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    [Fact]
    public void InsufficientCapturePreservesExistingTargetAndRecoveryInput()
    {
        var directory = CreateWorkDirectory();
        var targetPath = Path.Combine(directory, "recording.mp4");
        var capturePath = targetPath + ".dotcore-capture-insufficient";
        var protectedTarget = new byte[] { 0x70, 0x72, 0x6f, 0x74, 0x65, 0x63, 0x74, 0x65, 0x64 };
        var insufficientCapture = new byte[]
        {
            0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2'
        };
        try
        {
            File.WriteAllBytes(targetPath, protectedTarget);
            File.WriteAllBytes(capturePath, insufficientCapture);

            var result = Mp4RecordingRecovery.Recover(targetPath);

            Assert.Equal(Mp4RecordingRecoveryTier.NoRecoverableMedia, result.Tier);
            Assert.Equal(protectedTarget, File.ReadAllBytes(targetPath));
            Assert.True(File.Exists(capturePath));
            Assert.Equal(insufficientCapture, File.ReadAllBytes(capturePath));
            Assert.False(File.Exists(targetPath + ".dotcore-journal"));
        }
        finally
        {
            DeleteWorkDirectory(directory);
        }
    }

    private static void WriteInterruptedRecording(string targetPath, VideoFixture fixture, Mp4WriteMode mode)
    {
        using (var writer = new Mp4RecordingWriter(targetPath, new Mp4WriterOptions { Mode = mode }))
        {
            writer.SetVideoCodecConfiguration(fixture.Configuration);
            writer.SetAudioCodecConfiguration(FixtureData.AacConfiguration);
            foreach (var sample in CreateOrderedSamples(fixture))
            {
                if (sample.IsVideo)
                {
                    writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                        sample.Data,
                        sample.PresentationTimestamp,
                        sample.DecodeTimestamp,
                        sample.Duration,
                        sample.IsKeyFrame));
                }
                else
                {
                    writer.WriteAudioSample(new EncodedAudioSample(
                        sample.Data,
                        sample.PresentationTimestamp,
                        sample.DecodeTimestamp,
                        sample.Duration));
                }
            }
        }
    }

    private static async Task WriteInterruptedRecordingAsync(
        string targetPath,
        VideoFixture fixture,
        Mp4WriteMode mode)
    {
        using (var writer = await Mp4RecordingWriter.CreateAsync(
            targetPath,
            new Mp4WriterOptions { Mode = mode }))
        {
            await writer.SetVideoCodecConfigurationAsync(fixture.Configuration);
            await writer.SetAudioCodecConfigurationAsync(FixtureData.AacConfiguration);
            foreach (var sample in CreateOrderedSamples(fixture))
            {
                if (sample.IsVideo)
                {
                    await writer.WriteVideoNalUnitAsync(new EncodedVideoNalUnit(
                        sample.Data,
                        sample.PresentationTimestamp,
                        sample.DecodeTimestamp,
                        sample.Duration,
                        sample.IsKeyFrame));
                }
                else
                {
                    await writer.WriteAudioSampleAsync(new EncodedAudioSample(
                        sample.Data,
                        sample.PresentationTimestamp,
                        sample.DecodeTimestamp,
                        sample.Duration));
                }
            }
        }
    }

    private static IReadOnlyList<RecordedSample> CreateOrderedSamples(VideoFixture fixture)
    {
        var samples = new List<RecordedSample>();
        var audioDuration = TimeSpan.FromTicks(
            (long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / FixtureData.AacConfiguration.SampleRate));
        var audio = FixtureData.AacAccessUnits.Take(4).ToArray();
        var videoIndex = 0;
        var audioIndex = 0;
        while (videoIndex < fixture.Frames.Count || audioIndex < audio.Length)
        {
            var videoTimestamp = TimeSpan.FromMilliseconds(videoIndex * 40);
            var audioTimestamp = TimeSpan.FromTicks(audioDuration.Ticks * audioIndex);
            if (videoIndex < fixture.Frames.Count &&
                (audioIndex >= audio.Length || videoTimestamp <= audioTimestamp))
            {
                samples.Add(new RecordedSample(
                    true,
                    fixture.Frames[videoIndex],
                    videoTimestamp,
                    videoTimestamp,
                    TimeSpan.FromMilliseconds(40),
                    fixture.KeyFrames[videoIndex]));
                videoIndex++;
            }
            else
            {
                samples.Add(new RecordedSample(
                    false,
                    audio[audioIndex],
                    audioTimestamp,
                    audioTimestamp,
                    audioDuration,
                    false));
                audioIndex++;
            }
        }

        return samples;
    }

    private static void AssertRecoveredRoundTrip(string targetPath, VideoFixture fixture)
    {
        var expected = CreateOrderedSamples(fixture);
        var actualEventOrder = new List<string>();
        using (var input = File.OpenRead(targetPath))
        using (var reader = new Mp4Reader(input))
        {
            Assert.Equal(fixture.Configuration.Codec, reader.VideoConfiguration!.Codec);
            Assert.Equal(fixture.Configuration.Vps, reader.VideoConfiguration.Vps);
            Assert.Equal(fixture.Configuration.Sps, reader.VideoConfiguration.Sps);
            Assert.Equal(fixture.Configuration.Pps, reader.VideoConfiguration.Pps);
            Assert.Equal(fixture.Configuration.NalLengthSize, reader.VideoConfiguration.NalLengthSize);
            Assert.Equal(FixtureData.AacConfiguration.AudioSpecificConfig, reader.AudioConfiguration!.AudioSpecificConfig);

            reader.VideoNalUnitRead += (_, sample) => actualEventOrder.Add(DescribeVideo(sample));
            reader.AacSampleRead += (_, sample) => actualEventOrder.Add(DescribeAudio(sample));
            reader.Read();
        }

        var expectedEventOrder = expected.Select(Describe).ToArray();
        Assert.Equal(expectedEventOrder, actualEventOrder);
    }

    private static void AssertHeuristicRoundTrip(string targetPath, VideoFixture fixture)
    {
        using (var input = File.OpenRead(targetPath))
        using (var reader = new Mp4Reader(input))
        {
            Assert.Equal(fixture.Configuration.Codec, reader.VideoConfiguration!.Codec);
            Assert.Equal(fixture.Configuration.Vps, reader.VideoConfiguration.Vps);
            Assert.Equal(fixture.Configuration.Sps, reader.VideoConfiguration.Sps);
            Assert.Equal(fixture.Configuration.Pps, reader.VideoConfiguration.Pps);
            Assert.Equal(fixture.Configuration.NalLengthSize, reader.VideoConfiguration.NalLengthSize);
            Assert.Null(reader.AudioConfiguration);

            var video = reader.ReadVideoNalUnits().ToArray();
            var audio = reader.ReadAudioSamples().ToArray();
            Assert.Equal(fixture.Frames, video.Select(sample => sample.Data));
            Assert.Empty(audio);
            for (var index = 0; index < video.Length; index++)
            {
                Assert.Equal(TimeSpan.FromMilliseconds(index), video[index].PresentationTimestamp);
                Assert.Equal(TimeSpan.FromMilliseconds(index), video[index].DecodeTimestamp);
                Assert.Equal(TimeSpan.FromMilliseconds(1), video[index].Duration);
                Assert.Equal(fixture.KeyFrames[index], video[index].IsKeyFrame);
            }
        }
    }

    private static void WriteHeuristicCapture(string capturePath, VideoFixture fixture)
    {
        using (var capture = new FileStream(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            capture.Write(new byte[]
            {
                0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0,
                (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2',
                0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t'
            });
            WriteLengthPrefixedNal(capture, fixture.Configuration.Vps);
            WriteLengthPrefixedNal(capture, fixture.Configuration.Sps);
            WriteLengthPrefixedNal(capture, fixture.Configuration.Pps);
            foreach (var frame in fixture.Frames) WriteLengthPrefixedNal(capture, frame);
        }
    }

    private static void WriteLengthPrefixedNal(Stream output, byte[]? nal)
    {
        if (nal == null || nal.Length == 0) return;

        output.WriteByte((byte)(nal.Length >> 24));
        output.WriteByte((byte)(nal.Length >> 16));
        output.WriteByte((byte)(nal.Length >> 8));
        output.WriteByte((byte)nal.Length);
        output.Write(nal, 0, nal.Length);
    }

    private static void AssertExternalMediaTools(
        string path,
        string expectedVideoCodec,
        bool expectAac)
    {
        var ffprobe = RequireTool("ffprobe");
        var ffmpeg = RequireTool("ffmpeg");
        var probe = Run(ffprobe, "-v error -show_format -show_streams -of json " + Quote(path));
        Assert.Equal(0, probe.ExitCode);
        Assert.Contains("mov,mp4", probe.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedVideoCodec, probe.Stdout, StringComparison.OrdinalIgnoreCase);
        if (expectAac)
        {
            Assert.Contains("\"codec_name\": \"aac\"", probe.Stdout, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("\"codec_name\": \"aac\"", probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"codec_type\": \"audio\"", probe.Stdout, StringComparison.OrdinalIgnoreCase);
        }

        var decode = Run(ffmpeg, "-v error -i " + Quote(path) + " -map 0 -f null -");
        Assert.Equal(0, decode.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(decode.Stderr), Redact(decode.Stderr));
    }

    private static string RequireTool(string name)
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executableName))
            .FirstOrDefault(File.Exists);
        if (path == null) throw SkipException.ForSkip("Prerequisite executable is missing from PATH: " + executableName);
        return path;
    }

    private static ProcessResult Run(string executable, string arguments)
    {
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start " + executable + "."))
        {
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Redact(string value)
    {
        var builder = new StringBuilder(value ?? string.Empty);
        return builder.Length > 2000 ? builder.ToString(0, 2000) + "..." : builder.ToString();
    }

    private static string Describe(RecordedSample sample)
    {
        return (sample.IsVideo ? "V" : "A") + "|" +
               Convert.ToBase64String(sample.Data) + "|" +
               sample.PresentationTimestamp.Ticks + "|" +
               sample.DecodeTimestamp.Ticks + "|" +
               sample.Duration.Ticks + "|" +
               sample.IsKeyFrame;
    }

    private static string DescribeVideo(VideoNalUnitReadEventArgs sample)
    {
        return "V|" + Convert.ToBase64String(sample.Data) + "|" +
               sample.PresentationTimestamp.Ticks + "|" +
               sample.DecodeTimestamp.Ticks + "|" +
               sample.Duration.Ticks + "|" +
               sample.IsKeyFrame;
    }

    private static string DescribeAudio(AacSampleReadEventArgs sample)
    {
        return "A|" + Convert.ToBase64String(sample.Data) + "|" +
               sample.PresentationTimestamp.Ticks + "|" +
               sample.DecodeTimestamp.Ticks + "|" +
               sample.Duration.Ticks + "|False";
    }

    private static string CreateWorkDirectory()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "recording-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteWorkDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private readonly struct RecordedSample
    {
        public RecordedSample(
            bool isVideo,
            byte[] data,
            TimeSpan presentationTimestamp,
            TimeSpan decodeTimestamp,
            TimeSpan duration,
            bool isKeyFrame)
        {
            IsVideo = isVideo;
            Data = data;
            PresentationTimestamp = presentationTimestamp;
            DecodeTimestamp = decodeTimestamp;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
        }

        public bool IsVideo { get; }
        public byte[] Data { get; }
        public TimeSpan PresentationTimestamp { get; }
        public TimeSpan DecodeTimestamp { get; }
        public TimeSpan Duration { get; }
        public bool IsKeyFrame { get; }
    }

    private readonly struct ProcessResult
    {
        public ProcessResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int ExitCode { get; }
        public string Stdout { get; }
        public string Stderr { get; }
    }
}

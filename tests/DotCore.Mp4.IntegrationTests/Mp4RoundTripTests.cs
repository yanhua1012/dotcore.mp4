using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;
using Xunit.Sdk;

namespace DotCore.Mp4.IntegrationTests;

/// <summary>
/// MP4 寫入器與讀取器 Round-Trip 以及與 FFprobe / FFmpeg 互操作性測試套件。
/// </summary>
public sealed class Mp4RoundTripTests
{
    /// <summary>
    /// 驗證 Writer/Reader 矩陣在不同編解碼器與寫入模式下能完整保留 Payload、時間標記與組態。
    /// </summary>
    [InlineData(false, Mp4WriteMode.Progressive)]
    [InlineData(false, Mp4WriteMode.FastStart)]
    [InlineData(false, Mp4WriteMode.Fragmented)]
    [InlineData(true, Mp4WriteMode.Progressive)]
    [InlineData(true, Mp4WriteMode.FastStart)]
    [InlineData(true, Mp4WriteMode.Fragmented)]
    public void WriterReaderMatrixPreservesPayloadTimingAndConfiguration(bool h265, Mp4WriteMode mode)
    {
        AssertRoundTrip(h265 ? FixtureData.H265 : FixtureData.H264, mode);
    }

    [Theory]
    [InlineData(false, Mp4WriteMode.Progressive, "h264")]
    [InlineData(false, Mp4WriteMode.FastStart, "h264")]
    [InlineData(false, Mp4WriteMode.Fragmented, "h264")]
    [InlineData(true, Mp4WriteMode.Progressive, "hevc")]
    [InlineData(true, Mp4WriteMode.FastStart, "hevc")]
    [InlineData(true, Mp4WriteMode.Fragmented, "hevc")]
    public void GeneratedLayoutPassesFfprobeAndFfmpegValidation(
        bool h265,
        Mp4WriteMode mode,
        string expectedVideoCodec)
    {
        AssertExternalTools(h265 ? FixtureData.H265 : FixtureData.H264, mode, expectedVideoCodec);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderParsesFfmpegGeneratedFragmentedReference(bool h265)
    {
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        var sourcePath = Path.Combine(Path.GetTempPath(), "dotcore-mp4-source-" + Guid.NewGuid().ToString("N") + ".mp4");
        var referencePath = Path.Combine(Path.GetTempPath(), "dotcore-mp4-reference-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            FixtureData.WriteMixedFile(sourcePath, fixture);
            var ffmpeg = RequireTool("ffmpeg");
            var remux = Run(
                ffmpeg,
                "-v error -i " + Quote(sourcePath) +
                " -map 0 -c copy -movflags empty_moov+default_base_moof+frag_keyframe -f mp4 " +
                Quote(referencePath));
            Assert.Equal(0, remux.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(remux.Stderr), Redact(remux.Stderr));

            using var sourceReader = new Mp4Reader(File.OpenRead(sourcePath), leaveOpen: false);
            using var referenceReader = new Mp4Reader(File.OpenRead(referencePath), leaveOpen: false);
            Assert.Equal(sourceReader.VideoConfiguration!.Codec, referenceReader.VideoConfiguration!.Codec);
            Assert.Equal(
                sourceReader.ReadVideoNalUnits().Select(SnapshotVideo),
                referenceReader.ReadVideoNalUnits().Select(SnapshotVideo));
            AssertAudioEquivalent(
                sourceReader.ReadAudioSamples().ToArray(),
                referenceReader.ReadAudioSamples().ToArray());
            Assert.True(ContainsBox(File.ReadAllBytes(referencePath), "moof"));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(referencePath)) File.Delete(referencePath);
        }
    }

    [Theory]
    [InlineData(false, Mp4WriteMode.Progressive)]
    [InlineData(false, Mp4WriteMode.FastStart)]
    [InlineData(false, Mp4WriteMode.Fragmented)]
    [InlineData(true, Mp4WriteMode.Progressive)]
    [InlineData(true, Mp4WriteMode.FastStart)]
    [InlineData(true, Mp4WriteMode.Fragmented)]
    public async System.Threading.Tasks.Task AsyncWriterReaderMatrixPreservesPayloadTimingAndConfiguration(bool h265, Mp4WriteMode mode)
    {
        await AssertAsyncRoundTrip(h265 ? FixtureData.H265 : FixtureData.H264, mode);
    }

    [Theory]
    [InlineData(false, Mp4WriteMode.Progressive, "h264")]
    [InlineData(false, Mp4WriteMode.FastStart, "h264")]
    [InlineData(false, Mp4WriteMode.Fragmented, "h264")]
    [InlineData(true, Mp4WriteMode.Progressive, "hevc")]
    [InlineData(true, Mp4WriteMode.FastStart, "hevc")]
    [InlineData(true, Mp4WriteMode.Fragmented, "hevc")]
    public async System.Threading.Tasks.Task AsyncGeneratedLayoutPassesFfprobeAndFfmpegValidation(
        bool h265,
        Mp4WriteMode mode,
        string expectedVideoCodec)
    {
        await AssertAsyncExternalTools(h265 ? FixtureData.H265 : FixtureData.H264, mode, expectedVideoCodec);
    }

    [Theory]
    [InlineData(false, Mp4WriteMode.Progressive)]
    [InlineData(false, Mp4WriteMode.FastStart)]
    [InlineData(false, Mp4WriteMode.Fragmented)]
    [InlineData(true, Mp4WriteMode.Progressive)]
    [InlineData(true, Mp4WriteMode.FastStart)]
    [InlineData(true, Mp4WriteMode.Fragmented)]
    public async System.Threading.Tasks.Task AsyncOutputIsByteIdenticalToSyncOutput(bool h265, Mp4WriteMode mode)
    {
        var fixture = h265 ? FixtureData.H265 : FixtureData.H264;
        var syncPath = Path.Combine(Path.GetTempPath(), "dotcore-mp4-sync-parity-" + Guid.NewGuid().ToString("N") + ".mp4");
        var asyncPath = Path.Combine(Path.GetTempPath(), "dotcore-mp4-async-parity-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            FixtureData.WriteMixedFile(syncPath, fixture, mode);
            await FixtureData.WriteMixedFileAsync(asyncPath, fixture, mode);
            Assert.Equal(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(syncPath)),
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(asyncPath)));
        }
        finally
        {
            if (File.Exists(syncPath)) File.Delete(syncPath);
            if (File.Exists(asyncPath)) File.Delete(asyncPath);
        }
    }

    private static async System.Threading.Tasks.Task AssertAsyncRoundTrip(VideoFixture fixture, Mp4WriteMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotcore-mp4-async-roundtrip-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await FixtureData.WriteMixedFileAsync(path, fixture, mode);
            using var stream = File.OpenRead(path);
            using var reader = await Mp4Reader.CreateAsync(stream);
            Assert.Equal(fixture.Configuration.Codec, reader.VideoConfiguration!.Codec);
            Assert.Equal(fixture.Configuration.Vps, reader.VideoConfiguration.Vps);
            Assert.Equal(fixture.Configuration.Sps, reader.VideoConfiguration.Sps);
            Assert.Equal(fixture.Configuration.Pps, reader.VideoConfiguration.Pps);
            Assert.Equal(fixture.Configuration.NalLengthSize, reader.VideoConfiguration.NalLengthSize);
            Assert.Equal(FixtureData.AacConfiguration.AudioSpecificConfig, reader.AudioConfiguration!.AudioSpecificConfig);

            var video = reader.ReadVideoNalUnits().ToArray();
            var audio = reader.ReadAudioSamples().ToArray();
            Assert.Equal(fixture.Frames.Count, video.Length);
            for (var i = 0; i < video.Length; i++)
            {
                Assert.Equal(fixture.Frames[i], video[i].Data);
                Assert.Equal(TimeSpan.FromMilliseconds(i * 40), video[i].PresentationTimestamp);
                Assert.Equal(fixture.KeyFrames[i], video[i].IsKeyFrame);
            }

            Assert.Equal(4, audio.Length);
            Assert.Equal(FixtureData.AacAccessUnits[0], audio[0].Data);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async System.Threading.Tasks.Task AssertAsyncExternalTools(
        VideoFixture fixture,
        Mp4WriteMode mode,
        string expectedVideoCodec)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotcore-mp4-async-tools-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await FixtureData.WriteMixedFileAsync(path, fixture, mode);
            var ffprobe = RequireTool("ffprobe");
            var ffmpeg = RequireTool("ffmpeg");
            var probe = Run(ffprobe, "-v error -show_format -show_streams -of json " + Quote(path));
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("mov,mp4", probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedVideoCodec, probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("aac", probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(mode == Mp4WriteMode.Fragmented, ContainsBox(File.ReadAllBytes(path), "moof"));

            var decode = Run(ffmpeg, "-v error -i " + Quote(path) + " -map 0 -f null -");
            Assert.Equal(0, decode.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(decode.Stderr), Redact(decode.Stderr));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertExternalTools(
        VideoFixture fixture,
        Mp4WriteMode mode,
        string expectedVideoCodec)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotcore-mp4-integration-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            FixtureData.WriteMixedFile(path, fixture, mode);
            var ffprobe = RequireTool("ffprobe");
            var ffmpeg = RequireTool("ffmpeg");
            var probe = Run(ffprobe, "-v error -show_format -show_streams -of json " + Quote(path));
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("mov,mp4", probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedVideoCodec, probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("aac", probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(mode == Mp4WriteMode.Fragmented, ContainsBox(File.ReadAllBytes(path), "moof"));

            var decode = Run(ffmpeg, "-v error -i " + Quote(path) + " -map 0 -f null -");
            Assert.Equal(0, decode.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(decode.Stderr), Redact(decode.Stderr));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertRoundTrip(VideoFixture fixture, Mp4WriteMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotcore-mp4-roundtrip-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            FixtureData.WriteMixedFile(path, fixture, mode);
            using var stream = File.OpenRead(path);
            using var reader = new Mp4Reader(stream);
            Assert.Equal(fixture.Configuration.Codec, reader.VideoConfiguration!.Codec);
            Assert.Equal(fixture.Configuration.Vps, reader.VideoConfiguration.Vps);
            Assert.Equal(fixture.Configuration.Sps, reader.VideoConfiguration.Sps);
            Assert.Equal(fixture.Configuration.Pps, reader.VideoConfiguration.Pps);
            Assert.Equal(fixture.Configuration.NalLengthSize, reader.VideoConfiguration.NalLengthSize);
            Assert.Equal(FixtureData.AacConfiguration.AudioSpecificConfig, reader.AudioConfiguration!.AudioSpecificConfig);
            Assert.Equal(FixtureData.AacConfiguration.SampleRate, reader.AudioConfiguration.SampleRate);
            Assert.Equal(FixtureData.AacConfiguration.ChannelConfiguration, reader.AudioConfiguration.ChannelConfiguration);

            var video = reader.ReadVideoNalUnits().ToArray();
            var audio = reader.ReadAudioSamples().ToArray();
            Assert.Equal(fixture.Frames.Count, video.Length);
            for (var i = 0; i < video.Length; i++)
            {
                Assert.Equal(fixture.Frames[i], video[i].Data);
                Assert.Equal(TimeSpan.FromMilliseconds(i * 40), video[i].PresentationTimestamp);
                Assert.Equal(TimeSpan.FromMilliseconds(i * 40), video[i].DecodeTimestamp);
                Assert.Equal(TimeSpan.FromMilliseconds(40), video[i].Duration);
                Assert.Equal(fixture.KeyFrames[i], video[i].IsKeyFrame);
            }

            var duration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / FixtureData.AacConfiguration.SampleRate));
            Assert.Equal(4, audio.Length);
            Assert.Equal(FixtureData.AacAccessUnits[0], audio[0].Data);
            Assert.Equal(FixtureData.AacAccessUnits[1], audio[1].Data);
            Assert.Equal(TimeSpan.Zero, audio[0].DecodeTimestamp);
            Assert.Equal(duration, audio[0].Duration);
            Assert.Equal(duration, audio[1].DecodeTimestamp);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string SnapshotVideo(EncodedVideoNalUnit sample)
    {
        return Convert.ToBase64String(sample.Data) + "|" +
               sample.PresentationTimestamp.Ticks + "|" +
               sample.DecodeTimestamp.Ticks + "|" +
               sample.Duration.Ticks + "|" +
               sample.IsKeyFrame;
    }

    private static void AssertAudioEquivalent(
        EncodedAudioSample[] expected,
        EncodedAudioSample[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Data, actual[index].Data);
            Assert.InRange(
                Math.Abs(expected[index].PresentationTimestamp.Ticks - actual[index].PresentationTimestamp.Ticks),
                0,
                1);
            Assert.InRange(
                Math.Abs(expected[index].DecodeTimestamp.Ticks - actual[index].DecodeTimestamp.Ticks),
                0,
                1);
            Assert.Equal(expected[index].Duration, actual[index].Duration);
        }
    }

    private static bool ContainsBox(byte[] bytes, string type)
    {
        var marker = Encoding.ASCII.GetBytes(type);
        for (var index = 4; index <= bytes.Length - marker.Length; index++)
        {
            if (bytes.Skip(index).Take(marker.Length).SequenceEqual(marker)) return true;
        }

        return false;
    }

    private static string RequireTool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
        if (path == null) throw SkipException.ForSkip("Prerequisite executable is missing from PATH: " + name);
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start " + executable + ".");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Redact(string value)
    {
        var builder = new StringBuilder(value ?? string.Empty);
        return builder.Length > 2000 ? builder.ToString(0, 2000) + "..." : builder.ToString();
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

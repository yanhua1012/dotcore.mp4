using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;
using Xunit.Sdk;

namespace DotCore.Mp4.IntegrationTests;

public sealed class Mp4RoundTripTests
{
    [Fact]
    public void H264AndAacRoundTripPreservesPayloadTimingAndConfiguration()
    {
        AssertRoundTrip(FixtureData.H264);
    }

    [Fact]
    public void H265AndAacRoundTripPreservesPayloadTimingAndConfiguration()
    {
        AssertRoundTrip(FixtureData.H265);
    }

    [Fact]
    public void GeneratedH264Mp4PassesFfprobeAndFfmpegValidation()
    {
        AssertExternalTools(FixtureData.H264, "h264");
    }

    [Fact]
    public void GeneratedH265Mp4PassesFfprobeAndFfmpegValidation()
    {
        AssertExternalTools(FixtureData.H265, "hevc");
    }

    private static void AssertExternalTools(VideoFixture fixture, string expectedVideoCodec)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotcore-mp4-integration-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            FixtureData.WriteMixedFile(path, fixture);
            var ffprobe = RequireTool("ffprobe");
            var ffmpeg = RequireTool("ffmpeg");
            var probe = Run(ffprobe, "-v error -show_format -show_streams -of json " + Quote(path));
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("mov,mp4", probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedVideoCodec, probe.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("aac", probe.Stdout, StringComparison.OrdinalIgnoreCase);

            var decode = Run(ffmpeg, "-v error -i " + Quote(path) + " -map 0 -f null -");
            Assert.Equal(0, decode.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(decode.Stderr), Redact(decode.Stderr));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertRoundTrip(VideoFixture fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotcore-mp4-roundtrip-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            FixtureData.WriteMixedFile(path, fixture);
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
                Assert.True(video[i].IsKeyFrame);
            }

            var duration = TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * 1024.0 / FixtureData.AacConfiguration.SampleRate));
            Assert.Equal(2, audio.Length);
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

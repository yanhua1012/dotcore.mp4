using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.IntegrationTests;

public sealed class ConsoleSmokeTests
{
    [Theory]
    [InlineData(null, null, "progressive", VideoCodec.H264)]
    [InlineData("progressive", "h264", "progressive", VideoCodec.H264)]
    [InlineData("faststart", "h264", "faststart", VideoCodec.H264)]
    [InlineData("fragmented", "h264", "fragmented", VideoCodec.H264)]
    [InlineData("progressive", "h265", "progressive", VideoCodec.H265)]
    [InlineData("faststart", "h265", "faststart", VideoCodec.H265)]
    [InlineData("fragmented", "h265", "fragmented", VideoCodec.H265)]
    public void ConsoleWritesReopensSubscribesAndPrintsTimedSamples(
        string? requestedMode,
        string? requestedCodec,
        string expectedMode,
        VideoCodec expectedCodec)
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "samples", "DotCore.Mp4.Console", "DotCore.Mp4.Console.csproj");
        var output = Path.Combine(Path.GetTempPath(), "dotcore-console-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var arguments = "run --project " + Quote(project) + " --no-build -- " + Quote(output);
            if (requestedMode != null) arguments += " " + requestedMode;
            if (requestedCodec != null) arguments += " " + requestedCodec;
            var startInfo = new ProcessStartInfo("dotnet", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Console demo.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stderr);
            Assert.True(File.Exists(output), "Console did not create its output file.");
            Assert.Contains("Mode: " + expectedMode, stdout, StringComparison.Ordinal);
            Assert.Contains("Codec: " + (expectedCodec == VideoCodec.H265 ? "h265" : "h264"), stdout, StringComparison.Ordinal);
            if (expectedCodec == VideoCodec.H265)
            {
                Assert.Contains("Parsed H.265 VPS:", stdout, StringComparison.Ordinal);
                Assert.Contains("Parsed H.265 SPS:", stdout, StringComparison.Ordinal);
                Assert.Contains("Parsed H.265 PPS:", stdout, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Parsed H.264 SPS:", stdout, StringComparison.Ordinal);
                Assert.Contains("Parsed H.264 PPS:", stdout, StringComparison.Ordinal);
            }

            Assert.Contains("Parsed AAC: objectType=2 sampleRate=44100 channels=2 ASC=1210", stdout, StringComparison.Ordinal);
            var outputLines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var videoLines = outputLines.Where(line => line.StartsWith("Video NAL:", StringComparison.Ordinal)).ToArray();
            var audioLines = outputLines.Where(line => line.StartsWith("AAC sample:", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, videoLines.Length);
            Assert.Equal(4, audioLines.Length);
            Assert.All(videoLines, line =>
            {
                Assert.Contains(" pts=", line, StringComparison.Ordinal);
                Assert.Contains(" dts=", line, StringComparison.Ordinal);
                Assert.Contains(" duration=", line, StringComparison.Ordinal);
                Assert.Contains(" key=", line, StringComparison.Ordinal);
            });
            Assert.All(audioLines, line =>
            {
                Assert.Contains(" pts=", line, StringComparison.Ordinal);
                Assert.Contains(" dts=", line, StringComparison.Ordinal);
                Assert.Contains(" duration=", line, StringComparison.Ordinal);
            });

            using var stream = File.OpenRead(output);
            using var reader = new Mp4Reader(stream);
            Assert.Equal(expectedCodec, reader.VideoConfiguration!.Codec);
            Assert.Equal(new[] { true, false, true }, reader.ReadVideoNalUnits().Select(sample => sample.IsKeyFrame));
            Assert.Equal(4, reader.ReadAudioSamples().Count());
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void ConsoleRejectsUnknownModeWithoutCreatingSuccessOutput()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "samples", "DotCore.Mp4.Console", "DotCore.Mp4.Console.csproj");
        var output = Path.Combine(Path.GetTempPath(), "dotcore-console-invalid-" + Guid.NewGuid().ToString("N") + ".mp4");
        var startInfo = new ProcessStartInfo(
            "dotnet",
            "run --project " + Quote(project) + " --no-build -- " + Quote(output) + " unknown")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Console demo.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Usage:", stdout + stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ConsoleRejectsUnknownCodecWithoutCreatingSuccessOutput()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "samples", "DotCore.Mp4.Console", "DotCore.Mp4.Console.csproj");
        var output = Path.Combine(Path.GetTempPath(), "dotcore-console-invalid-codec-" + Guid.NewGuid().ToString("N") + ".mp4");
        var startInfo = new ProcessStartInfo(
            "dotnet",
            "run --project " + Quote(project) + " --no-build -- " + Quote(output) + " progressive unknown")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Console demo.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Usage:", stdout + stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DotCore.Mp4.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate DotCore.Mp4.sln for the Console smoke test.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

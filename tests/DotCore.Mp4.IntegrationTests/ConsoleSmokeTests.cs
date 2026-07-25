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
    [InlineData(null, "progressive")]
    [InlineData("progressive", "progressive")]
    [InlineData("faststart", "faststart")]
    [InlineData("fragmented", "fragmented")]
    public void ConsoleWritesReopensSubscribesAndPrintsTimedSamples(string? requestedMode, string expectedMode)
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "samples", "DotCore.Mp4.Console", "DotCore.Mp4.Console.csproj");
        var output = Path.Combine(Path.GetTempPath(), "dotcore-console-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var arguments = "run --project " + Quote(project) + " --no-build -- " + Quote(output);
            if (requestedMode != null) arguments += " " + requestedMode;
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
            Assert.Contains("Parsed H.264 SPS:", stdout, StringComparison.Ordinal);
            Assert.Contains("Parsed H.264 PPS:", stdout, StringComparison.Ordinal);
            Assert.Contains("Parsed AAC: objectType=2 sampleRate=44100 channels=2 ASC=1210", stdout, StringComparison.Ordinal);
            Assert.Contains("Video NAL:", stdout, StringComparison.Ordinal);
            Assert.Contains("AAC sample:", stdout, StringComparison.Ordinal);

            using var stream = File.OpenRead(output);
            using var reader = new Mp4Reader(stream);
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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DotCore.Mp4.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate DotCore.Mp4.sln for the Console smoke test.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

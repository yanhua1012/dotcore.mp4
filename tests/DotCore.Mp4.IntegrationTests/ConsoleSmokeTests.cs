using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.IntegrationTests;

public sealed class ConsoleSmokeTests
{
    [Fact]
    public void ConsoleWritesReopensSubscribesAndPrintsTimedSamples()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "samples", "DotCore.Mp4.Console", "DotCore.Mp4.Console.csproj");
        var output = Path.Combine(Path.GetTempPath(), "dotcore-console-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var startInfo = new ProcessStartInfo("dotnet", "run --project " + Quote(project) + " --no-build -- " + Quote(output))
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
            Assert.Contains("H.264 SPS:", stdout, StringComparison.Ordinal);
            Assert.Contains("AAC:", stdout, StringComparison.Ordinal);
            Assert.Contains("Video NAL:", stdout, StringComparison.Ordinal);
            Assert.Contains("AAC sample:", stdout, StringComparison.Ordinal);

            using var stream = File.OpenRead(output);
            using var reader = new Mp4Reader(stream);
            Assert.Equal(2, reader.ReadVideoNalUnits().Count());
            Assert.Equal(2, reader.ReadAudioSamples().Count());
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DotCore.Mp4.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate DotCore.Mp4.sln for the Console smoke test.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

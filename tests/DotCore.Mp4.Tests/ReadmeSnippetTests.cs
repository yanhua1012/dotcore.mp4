using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DotCore.Mp4.Tests;

/// <summary>
/// Extracts the marker-delimited C# usage snippets from README.md and compiles them
/// against the current DotCore.Mp4 public surface, so the docs cannot drift from the API.
/// </summary>
public sealed class ReadmeSnippetTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void ReadmeAsyncSnippetsCompileAgainstCurrentLibrary()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        var snippets = ExtractMarkedSnippets(readme);
        Assert.NotEmpty(snippets);

        var compilation = BuildCompilation(snippets);
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(diagnostics.Count == 0,
            "README snippets failed to compile:\n" +
            string.Join("\n", diagnostics.Select(d => d.ToString())));
    }

    private static IReadOnlyList<string> ExtractMarkedSnippets(string readme)
    {
        var result = new List<string>();
        var markerStart = "<!-- snippet:";
        var markerEnd = "<!-- endsnippet -->";
        var fence = "```csharp";
        int search = 0;
        while (search < readme.Length)
        {
            var start = readme.IndexOf(markerStart, search, StringComparison.Ordinal);
            if (start < 0) break;
            var fencePos = readme.IndexOf(fence, start, StringComparison.Ordinal);
            var end = readme.IndexOf(markerEnd, start, StringComparison.Ordinal);
            if (fencePos < 0 || end < 0 || fencePos > end) break;
            var codeStart = fencePos + fence.Length;
            var codeEnd = readme.IndexOf("```", codeStart, StringComparison.Ordinal);
            if (codeEnd < 0) break;
            result.Add(readme.Substring(codeStart, codeEnd - codeStart));
            search = end + markerEnd.Length;
        }

        return result;
    }

    private static CSharpCompilation BuildCompilation(IReadOnlyList<string> snippets)
    {
        var stub = @"
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotCore.Mp4;

internal static class ReadmeSnippetHost
{
    private static readonly VideoCodecConfiguration videoConfiguration =
        VideoCodecConfiguration.CreateH264(new byte[] { 0x67, 0x42, 0x00, 0x1e }, new byte[] { 0x68, 0xce, 0x06, 0xe2 }, 4, 16, 16);
    private static readonly AacCodecConfiguration aacConfiguration =
        new AacCodecConfiguration(new byte[] { 0x12, 0x10 }, 44100, 2);
    private static readonly byte[] nal = new byte[] { 0x65, 0x01 };
    private static readonly byte[] aacBytes = new byte[] { 0x21, 0x10 };
    private static readonly TimeSpan pts = TimeSpan.Zero;
    private static readonly TimeSpan dts = TimeSpan.Zero;
    private static readonly TimeSpan duration = TimeSpan.FromMilliseconds(40);
    private const bool isKeyFrame = true;

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
";

        var builder = new System.Text.StringBuilder();
        builder.Append(stub);
        foreach (var snippet in snippets)
        {
            // Strip README's local variable declarations that would collide with the stub.
            var indented = IndentSnippet(snippet);
            builder.Append(indented);
        }

        builder.Append("await Task.CompletedTask;\n    }\n}\n");

        var tree = CSharpSyntaxTree.ParseText(builder.ToString());
        var libraryPath = Path.Combine(RepoRoot, "src", "DotCore.Mp4", "bin", "Debug", "netstandard2.0", "DotCore.Mp4.dll");
        if (!File.Exists(libraryPath))
        {
            libraryPath = Path.Combine(RepoRoot, "src", "DotCore.Mp4", "bin", "Release", "netstandard2.0", "DotCore.Mp4.dll");
        }

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(FileStream).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Mp4Reader).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(FindNetStandardReference()),
        };

        return CSharpCompilation.Create(
            "ReadmeSnippetCheck",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string IndentSnippet(string snippet)
    {
        var lines = snippet.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            sb.Append("        ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DotCore.Mp4.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root for README snippet test.");
    }

    private static string FindNetStandardReference()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var candidate = Path.Combine(runtimeDir, "netstandard.dll");
        if (File.Exists(candidate)) return candidate;
        throw new InvalidOperationException("Could not locate netstandard.dll for README snippet compilation.");
    }
}
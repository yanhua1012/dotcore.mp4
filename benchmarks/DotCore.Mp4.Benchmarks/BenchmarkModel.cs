using System.Text.Json.Serialization;
using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

public enum BenchmarkOperation
{
    ReaderConstructor,
    ReaderDelivery,
    WriterIngestion,
    FragmentFlush,
    FastStartFinalization
}

public enum VideoInputKind
{
    None,
    Raw,
    AnnexB
}

public enum NalShape
{
    None,
    Single,
    Multi,
    Tiny
}

public sealed record BenchmarkScenario(
    string Id,
    BenchmarkOperation Operation,
    VideoCodec? Codec,
    Mp4WriteMode? Layout,
    VideoInputKind Input,
    NalShape Shape,
    string Payload,
    int LogicalPayloadBytes,
    int SampleCount,
    int NalCount,
    int GopLength,
    bool Events,
    bool DataAccess,
    string StreamKind,
    bool Required,
    bool AllocationGate)
{
    public override string ToString() => Id;
}

internal sealed class BenchmarkRun
{
    public int SchemaVersion { get; set; } = 1;
    public string HarnessVersion { get; set; } = HarnessInfo.Version;
    public string SourceCommit { get; set; } = string.Empty;
    public string SourceStateSha256 { get; set; } = string.Empty;
    public bool DirtyWorktree { get; set; }
    public string Command { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Processor { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public string ResultPath { get; set; } = string.Empty;
    public string CanonicalPayloadSha256 { get; set; } = string.Empty;
    [JsonIgnore]
    public string LoadedPath { get; set; } = string.Empty;
    public List<BenchmarkResult> Results { get; set; } = new();
}

internal sealed class BenchmarkResult
{
    public string Identity { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool AllocationGate { get; set; }
    public int LogicalPayloadBytes { get; set; }
    public int SampleCount { get; set; }
    public int NalCount { get; set; }
    public int GopLength { get; set; }
    public long Operations { get; set; }
    public double MedianNanoseconds { get; set; }
    public double OperationsPerSecond { get; set; }
    public double AllocatedBytesPerOperation { get; set; }
    public long Gen0Collections { get; set; }
    public long Gen1Collections { get; set; }
    public long Gen2Collections { get; set; }
    public long StreamWriteCalls { get; set; }
    public long StreamReadCalls { get; set; }
    public long StreamSeekCalls { get; set; }
    public long StreamSetLengthCalls { get; set; }
    public long StreamBytesWritten { get; set; }
    public long OutputGrowthEvents { get; set; }
}

internal sealed class ComparisonReport
{
    public bool Passed { get; set; }
    public int BaselineRunCount { get; set; }
    public int CandidateRunCount { get; set; }
    public List<ComparisonResult> Results { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

internal sealed class ComparisonResult
{
    public string Identity { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool AllocationGate { get; set; }
    public double BaselineAllocatedBytes { get; set; }
    public double CandidateAllocatedBytes { get; set; }
    public double AllocationReductionPercent { get; set; }
    public double BaselineOperationsPerSecond { get; set; }
    public double CandidateOperationsPerSecond { get; set; }
    public double ThroughputChangePercent { get; set; }
    public bool Passed { get; set; }
    public List<string> Errors { get; set; } = new();
}

internal static class HarnessInfo
{
    public const string Version = "1.0.0";
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BenchmarkRun))]
[JsonSerializable(typeof(ComparisonReport))]
[JsonSerializable(typeof(CompatibilityBaseline))]
[JsonSerializable(typeof(FixedOutputBaseline))]
internal partial class BenchmarkJsonContext : JsonSerializerContext;

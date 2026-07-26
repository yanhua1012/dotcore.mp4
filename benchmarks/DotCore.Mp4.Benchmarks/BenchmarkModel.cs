using System.Text.Json.Serialization;
using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

/// <summary>
/// 基準測試操作項目的列舉。
/// </summary>
public enum BenchmarkOperation
{
    /// <summary>
    /// Reader 建構子測試。
    /// </summary>
    ReaderConstructor,
    /// <summary>
    /// Reader 遞送測試。
    /// </summary>
    ReaderDelivery,
    /// <summary>
    /// Writer 寫入測試。
    /// </summary>
    WriterIngestion,
    /// <summary>
    /// Fragment 刷寫測試。
    /// </summary>
    FragmentFlush,
    /// <summary>
    /// FastStart 最終化測試。
    /// </summary>
    FastStartFinalization,
    /// <summary>
    /// 非同步 Reader Snapshot 測試。
    /// </summary>
    AsyncReaderSnapshot,
    /// <summary>
    /// 非同步 Writer 寫入測試。
    /// </summary>
    AsyncWriterIngestion,
    /// <summary>
    /// 非同步 Fragment 刷寫測試。
    /// </summary>
    AsyncFragmentFlush,
    /// <summary>
    /// 非同步 FastStart 最終化測試。
    /// </summary>
    AsyncFastStartFinalization,
    /// <summary>
    /// 非同步並行度測試。
    /// </summary>
    AsyncConcurrency
}

/// <summary>
/// I/O 模式列舉 (同步 / 非同步)。
/// </summary>
public enum IoMode
{
    /// <summary>
    /// 同步 I/O 模式。
    /// </summary>
    Sync,
    /// <summary>
    /// 非同步 I/O 模式。
    /// </summary>
    Async
}

/// <summary>
/// 測試串流種類列舉。
/// </summary>
public enum StreamKind
{
    /// <summary>
    /// 無串流。
    /// </summary>
    None,
    /// <summary>
    /// 預建記憶體串流。
    /// </summary>
    PrebuiltMemory,
    /// <summary>
    /// 預設大小計數記憶體串流。
    /// </summary>
    CountingPreSizedMemory,
    /// <summary>
    /// 非同步預設大小記憶體串流。
    /// </summary>
    AsyncPreSizedMemory,
    /// <summary>
    /// 非同步檔案串流。
    /// </summary>
    AsyncFile,
    /// <summary>
    /// 非同步受控閘門串流。
    /// </summary>
    AsyncGated
}

/// <summary>
/// 視訊輸入類型列舉。
/// </summary>
public enum VideoInputKind
{
    /// <summary>
    /// 無輸入。
    /// </summary>
    None,
    /// <summary>
    /// 原始 NAL 陣列。
    /// </summary>
    Raw,
    /// <summary>
    /// Annex B 位元組流。
    /// </summary>
    AnnexB
}

/// <summary>
/// NAL 結構形狀列舉。
/// </summary>
public enum NalShape
{
    /// <summary>
    /// 無。
    /// </summary>
    None,
    /// <summary>
    /// 單一 NAL。
    /// </summary>
    Single,
    /// <summary>
    /// 多個 NAL。
    /// </summary>
    Multi,
    /// <summary>
    /// 微型 NAL。
    /// </summary>
    Tiny
}

/// <summary>
/// 基準測試情境定義記錄。
/// </summary>
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
    bool AllocationGate,
    IoMode IoMode = IoMode.Sync,
    int Concurrency = 1,
    long DelayTicks = 0)
{
    /// <inheritdoc/>
    public override string ToString() => Id;
}

/// <summary>
/// 基準測試單次執行結果報告模型。
/// </summary>
internal sealed class BenchmarkRun
{
    /// <summary>
    /// Schema 版本。
    /// </summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>
    /// 測試 Harness 版本。
    /// </summary>
    public string HarnessVersion { get; set; } = HarnessInfo.Version;
    /// <summary>
    /// Git Commit Hash。
    /// </summary>
    public string SourceCommit { get; set; } = string.Empty;
    /// <summary>
    /// 原始碼狀態 SHA-256 Hash。
    /// </summary>
    public string SourceStateSha256 { get; set; } = string.Empty;
    /// <summary>
    /// 工作區是否有未提交變更。
    /// </summary>
    public bool DirtyWorktree { get; set; }
    /// <summary>
    /// 執行的命令列。
    /// </summary>
    public string Command { get; set; } = string.Empty;
    /// <summary>
    /// 執行階段資訊。
    /// </summary>
    public string Runtime { get; set; } = string.Empty;
    /// <summary>
    /// 作業系統資訊。
    /// </summary>
    public string OperatingSystem { get; set; } = string.Empty;
    /// <summary>
    /// 處理器資訊。
    /// </summary>
    public string Processor { get; set; } = string.Empty;
    /// <summary>
    /// 建立時間 (UTC)。
    /// </summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>
    /// 結果檔路徑。
    /// </summary>
    public string ResultPath { get; set; } = string.Empty;
    /// <summary>
    /// 規範 Payload SHA-256 Hash。
    /// </summary>
    public string CanonicalPayloadSha256 { get; set; } = string.Empty;
    /// <summary>
    /// 載入檔路徑 (不序列化)。
    /// </summary>
    [JsonIgnore]
    public string LoadedPath { get; set; } = string.Empty;
    /// <summary>
    /// 測試結果集合。
    /// </summary>
    public List<BenchmarkResult> Results { get; set; } = new();
}

/// <summary>
/// 單一情境基準測試數據結果。
/// </summary>
internal sealed class BenchmarkResult
{
    /// <summary>
    /// 情境識別碼。
    /// </summary>
    public string Identity { get; set; } = string.Empty;
    /// <summary>
    /// 指示是否為必須通過的情境。
    /// </summary>
    public bool Required { get; set; }
    /// <summary>
    /// 指示是否進行記憶體配置關卡檢查。
    /// </summary>
    public bool AllocationGate { get; set; }
    /// <summary>
    /// 邏輯 Payload 位元組數。
    /// </summary>
    public int LogicalPayloadBytes { get; set; }
    /// <summary>
    /// 樣本數。
    /// </summary>
    public int SampleCount { get; set; }
    /// <summary>
    /// NAL 數。
    /// </summary>
    public int NalCount { get; set; }
    /// <summary>
    /// GOP 長度。
    /// </summary>
    public int GopLength { get; set; }
    /// <summary>
    /// I/O 模式。
    /// </summary>
    public IoMode IoMode { get; set; }
    /// <summary>
    /// 串流種類。
    /// </summary>
    public string StreamKind { get; set; } = string.Empty;
    /// <summary>
    /// 並行度。
    /// </summary>
    public int Concurrency { get; set; } = 1;
    /// <summary>
    /// 延遲 Ticks。
    /// </summary>
    public long DelayTicks { get; set; }
    /// <summary>
    /// 總執行操作數。
    /// </summary>
    public long Operations { get; set; }
    /// <summary>
    /// 中位數耗時 (納秒)。
    /// </summary>
    public double MedianNanoseconds { get; set; }
    /// <summary>
    /// 每秒操作數 (Ops/s)。
    /// </summary>
    public double OperationsPerSecond { get; set; }
    /// <summary>
    /// 每次操作分配記憶體 (位元組)。
    /// </summary>
    public double AllocatedBytesPerOperation { get; set; }
    /// <summary>
    /// Gen0 GC 次數。
    /// </summary>
    public long Gen0Collections { get; set; }
    /// <summary>
    /// Gen1 GC 次數。
    /// </summary>
    public long Gen1Collections { get; set; }
    /// <summary>
    /// Gen2 GC 次數。
    /// </summary>
    public long Gen2Collections { get; set; }
    /// <summary>
    /// 串流 Write 呼叫數。
    /// </summary>
    public long StreamWriteCalls { get; set; }
    /// <summary>
    /// 串流 Read 呼叫數。
    /// </summary>
    public long StreamReadCalls { get; set; }
    /// <summary>
    /// 串流 Seek 呼叫數。
    /// </summary>
    public long StreamSeekCalls { get; set; }
    /// <summary>
    /// 串流 SetLength 呼叫數。
    /// </summary>
    public long StreamSetLengthCalls { get; set; }
    /// <summary>
    /// 串流寫入總位元組數。
    /// </summary>
    public long StreamBytesWritten { get; set; }
    /// <summary>
    /// 輸出容量擴增事件數。
    /// </summary>
    public long OutputGrowthEvents { get; set; }
    /// <summary>
    /// 非同步 Write 呼叫數。
    /// </summary>
    public long AsyncWriteCalls { get; set; }
    /// <summary>
    /// 非同步 Read 呼叫數。
    /// </summary>
    public long AsyncReadCalls { get; set; }
    /// <summary>
    /// 同步降級呼叫數。
    /// </summary>
    public long SyncFallbackCalls { get; set; }
    /// <summary>
    /// 最大在途操作數。
    /// </summary>
    public long MaxOutstandingIo { get; set; }
    /// <summary>
    /// 已完成操作數。
    /// </summary>
    public long CompletedOperations { get; set; }
    /// <summary>
    /// 同步完成的操作數。
    /// </summary>
    public long SynchronouslyCompletedOperations { get; set; }
    /// <summary>
    /// 同步完成比例。
    /// </summary>
    public double SynchronousCompletionRatio { get; set; }
}

/// <summary>
/// 基準測試比較報告模型。
/// </summary>
internal sealed class ComparisonReport
{
    /// <summary>
    /// 指示整體比較是否通過。
    /// </summary>
    public bool Passed { get; set; }
    /// <summary>
    /// 基線執行檔案數。
    /// </summary>
    public int BaselineRunCount { get; set; }
    /// <summary>
    /// 候選執行檔案數。
    /// </summary>
    public int CandidateRunCount { get; set; }
    /// <summary>
    /// 比較結果集合。
    /// </summary>
    public List<ComparisonResult> Results { get; set; } = new();
    /// <summary>
    /// 錯誤訊息集合。
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 單一情境基準測試數據比較結果。
/// </summary>
internal sealed class ComparisonResult
{
    /// <summary>
    /// 情境識別碼。
    /// </summary>
    public string Identity { get; set; } = string.Empty;
    /// <summary>
    /// 指示是否為必須通過的情境。
    /// </summary>
    public bool Required { get; set; }
    /// <summary>
    /// 指示是否進行記憶體配置關卡檢查。
    /// </summary>
    public bool AllocationGate { get; set; }
    /// <summary>
    /// 基線記憶體配置位元組數。
    /// </summary>
    public double BaselineAllocatedBytes { get; set; }
    /// <summary>
    /// 候選記憶體配置位元組數。
    /// </summary>
    public double CandidateAllocatedBytes { get; set; }
    /// <summary>
    /// 記憶體配置降低百分比。
    /// </summary>
    public double AllocationReductionPercent { get; set; }
    /// <summary>
    /// 基線 Ops/s。
    /// </summary>
    public double BaselineOperationsPerSecond { get; set; }
    /// <summary>
    /// 候選 Ops/s。
    /// </summary>
    public double CandidateOperationsPerSecond { get; set; }
    /// <summary>
    /// 吞吐量變動百分比。
    /// </summary>
    public double ThroughputChangePercent { get; set; }
    /// <summary>
    /// 指示該項比較是否通過。
    /// </summary>
    public bool Passed { get; set; }
    /// <summary>
    /// 錯誤訊息集合。
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 測試 Harness 相關資訊。
/// </summary>
internal static class HarnessInfo
{
    /// <summary>
    /// Harness 版本字串。
    /// </summary>
    public const string Version = "1.0.0";
}

/// <summary>
/// JSON 序列化 Context。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BenchmarkRun))]
[JsonSerializable(typeof(ComparisonReport))]
[JsonSerializable(typeof(CompatibilityBaseline))]
[JsonSerializable(typeof(FixedOutputBaseline))]
internal partial class BenchmarkJsonContext : JsonSerializerContext;

## Why

`Mp4Reader` 與 `Mp4Writer` 目前只呼叫同步 `Stream` I/O；當檔案或遠端儲存體延遲完成時，caller thread 會在 snapshot、payload output、fragment flush、faststart relocation 與 finalization 期間被占用。元件應保留既有同步/阻塞 contract，同時提供真正使用 `Stream.ReadAsync`/`WriteAsync` 的 additive async API，讓高併發工作負載能在 I/O 等待期間釋放執行緒，並以可重現測試與 benchmark 說明適用場景及成本。

## What Changes

- 為 `Mp4Reader` 新增 async factory，以 cancellable async snapshot 建立 Reader；建立後的 parsing、events 與可重複列舉仍維持既有 memory-only 同步行為。
- 為 `Mp4Writer` 新增 async factory 與 canonical async sample/finalization methods，讓 progressive payload output、fragmented startup/flush、faststart relocation 及 finalization 的外部 Stream I/O 全程走 async path。
- 完整保留既有 constructors、同步 methods/aliases、預設 progressive mode、public defensive-copy、exception、stream ownership、resource limits、timestamps、events、三種 layout 與 MP4 bytes；不以 `Task.Run` 或 sync-over-async 包裝既有方法。
- 定義 same-writer operation overlap、cancellation、partial-output failure、terminal fault state、成功 finalization idempotency 與 factory 失敗時的 caller-owned Stream 語意。
- 擴充 unit/integration test projects，以 instrumented delayed async Streams、取消/失敗注入、三種 layout 的 sync/async byte comparison、真實 file-backed round-trip 及 FFmpeg interoperability 證明行為。
- 擴充 `samples/DotCore.Mp4.Console`，在維持既有 invocation 預設行為下加入明確 async I/O demo，並由 Console smoke test 驗證 observable mode、codec、I/O path 與 round-trip output。
- 擴充 `benchmarks/DotCore.Mp4.Benchmarks`，分開量測同步與非同步 Reader/Writer 的 latency、throughput、allocation、GC、Stream call counts、同步完成比例及 bounded-concurrency scalability；`MemoryStream`、file-backed 與真正延遲完成的 async Stream 不混為同一結論。
- 更新 `README.md`，提供 sync/async API 使用方式、demo/benchmark commands、取消與 fault-state contract、底層 Stream 實作限制及量測結果解讀。

## Capabilities

### New Capabilities

- 無。

### Modified Capabilities

- `mp4-stream-demuxing`: 新增以 async snapshot factory 建立 Reader 的 contract，以及 cancellation、position restore、ownership 與同步列舉相容性。
- `mp4-stream-muxing`: 新增 additive async Writer 建立、sample ingestion、fragment flush、faststart relocation、finalization、overlap 與 partial-output failure contract。
- `mp4-component-verification`: 將 async API 的 unit/integration、Console demo、benchmark、public compatibility、fixed-output 與 README 驗證納入完成條件。

## Impact

- Production：`src/DotCore.Mp4/Mp4Reader.cs`、`Mp4Writer.cs` 與必要的 internal Stream I/O helpers；維持 dependency-free `netstandard2.0`，public async members 使用相容的 `Task`、`CancellationToken` 與 `byte[]` Stream overload。
- Verification：`tests/DotCore.Mp4.Tests`、`tests/DotCore.Mp4.IntegrationTests`、`samples/DotCore.Mp4.Console`、`benchmarks/DotCore.Mp4.Benchmarks`、tracked compatibility/fixed-output baselines 與 `README.md`。
- 風險屬 medium：async suspension 擴大 Writer state-machine surface，且取消或 I/O failure 可能留下 physical partial output；以 fail-fast overlap guard、terminal fault state、tests-first slices 與 sync rollback path控制。
- Rollback 可移除新增 async public members及其獨立 internal path，恢復只支援既有同步 API；沒有資料 migration、格式版本或 production dependency 變更。

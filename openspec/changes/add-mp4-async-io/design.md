## Context

Production library 是 dependency-free `netstandard2.0`。`Mp4Reader` 目前在 constructor 以 `Length`、`Seek` 與同步 `Read` 建立最多 256 MiB 的完整 snapshot，之後所有 parsing、events 與 enumeration 都只存取 managed memory。`Mp4Writer` 的 progressive/faststart constructor 會立即寫出 `ftyp` 與 extended `mdat` header；後續同步 I/O 分布於 audio payload、pending video access unit、fragmented initial metadata/fragment flush、progressive metadata、faststart 64 KiB relocation 與 finalization。

既有同步 public API、aliases、stream capability/ownership、defensive-copy、resource guards、timestamp/error semantics、三種 layout、fixed output bytes 與 Reader round-trip 都是 compatibility constraints。Async 的目標是讓真正支援非阻塞 async 的底層 Stream 在等待期間不占用 caller thread；它不是單一 operation throughput 一定提升的承諾。`Position`、`Length`、`Seek` 與 `SetLength` 沒有 async 對應，仍屬必要的同步 control operations。

## Goals / Non-Goals

**Goals:**

- 以 additive、具正體中文 XML documentation 的 `Task`/`CancellationToken` API，提供 Reader async snapshot 與 Writer 三種 layout 的 async external Stream I/O。
- 保留專用同步 path；sync 與 async 只共用 validation、state transition、metadata building 與 payload planning，不使用 `Task.Run`、`.Result`、`.Wait()` 或 `.GetAwaiter().GetResult()`。
- 允許同一 Writer 依序混用 sync/async calls，但 fail-fast 拒絕 overlap，並在 async partial-output failure/cancellation 後阻止 unsafe reuse。
- 以 unit/integration tests、Console demo、benchmark harness、compatibility/fixed-output baselines 與 README 提供可執行證據。
- 維持 dependency-free `netstandard2.0` production target，並保持既有同步 throughput required scenarios 不退化超過 10%。

**Non-Goals:**

- 不新增 Reader async enumeration；factory 完成後沒有 Stream I/O，`ReadVideoNalUnits()`、`ReadAudioSamples()`、`Read()` 與 aliases 繼續同步。
- 不將 Reader 改成 non-seekable、tail-follow、on-demand 或 single-pass streaming。
- 不新增 public `Memory<byte>`/`ReadOnlyMemory<byte>`、`IAsyncEnumerable`、`ValueTask`、`IAsyncDisposable` 或 production package。
- 不新增 async aliases，例如 `WriteVideoAsync`、`WriteAacSampleAsync`、`CompleteAsync` 或 `FinishAsync`；先維持最小 canonical public surface。
- 不暗示 `FinalizeFileAsync()` 會呼叫 `FlushAsync()`、強制 durable storage 或提供原子檔案交付。
- 不重新設計 MP4 layout、codec/sample model、faststart buffer size、fragment boundary 或既有同步 I/O failure recovery contract。

## Decisions

### 1. Public surface 採最小 additive `Task` API

新增下列 members，並將 `CancellationToken` 放在最後：

```csharp
public static Task<Mp4Reader> CreateAsync(
    Stream input,
    bool leaveOpen = true,
    CancellationToken cancellationToken = default);

public static Task<Mp4Writer> CreateAsync(
    Stream output,
    bool leaveOpen = true,
    CancellationToken cancellationToken = default);

public static Task<Mp4Writer> CreateAsync(
    Stream output,
    Mp4WriterOptions options,
    bool leaveOpen = true,
    CancellationToken cancellationToken = default);

public Task WriteVideoNalUnitAsync(
    EncodedVideoNalUnit sample,
    CancellationToken cancellationToken = default);

public Task WriteAudioSampleAsync(
    EncodedAudioSample sample,
    CancellationToken cancellationToken = default);

public Task FinalizeFileAsync(
    CancellationToken cancellationToken = default);
```

`Task` 與 `Stream.ReadAsync(byte[], int, int, CancellationToken)`/`WriteAsync(byte[], int, int, CancellationToken)` 可由既有 `netstandard2.0` target 使用，不需新增 dependency。Library-owned awaits 使用 `ConfigureAwait(false)`。Reader/Writer 統一使用 `CreateAsync`，不再增加同義 `OpenAsync`；codec configuration 沒有 I/O，維持同步。

替代方案是只新增 Writer async write/finalize methods，但 progressive/faststart constructor 已先同步寫 header，不能稱為完整 async external-write path，因此保留 async factory。Fragmented factory 完成 capability/options snapshot，但仍依既有語意延後 initial metadata 至第一個 media sample。

### 2. Reader 只非同步化 snapshot

`CreateAsync` 先執行和 constructor 相同的 null/readable/seekable、declared length 與 256 MiB guard，保存 original position，seek 至 0，使用 cancellable `ReadAsync` loop 填滿單一 snapshot，並在 `finally` 嘗試恢復 original position。Async read、取消、truncated input 或 parsing 失敗都不關閉 caller Stream；只有成功回傳的 Reader 在日後 `Dispose()` 且 `leaveOpen:false` 時接管關閉責任。Restore成功時保留原位置；restore本身失敗時依既有同步`finally`語意由restore exception取代先前read/cancellation failure，且不回傳Reader。

Snapshot 完成後先檢查 cancellation，再同步執行既有 parser；不以 `Task.Run` 搬移 CPU parsing，也不承諾 token 能中斷 parser 內部每一個 CPU loop。Async 與 sync factory 將共用 snapshot validation、track parsing 與 object initialization，確保 codec、payload、events、exceptions 與 delivered-sample lifetime 相同。

### 3. Writer 將 metadata planning 與 external output 分離

Public sync/async paths共用：

- options/capability/timestamp/configuration/resource validation；
- pending access-unit 與 fragment selection；
- 在 internal `MemoryStream` 同步建立 `ftyp`、`moov`、`moof`、`mdat` header 及其他小型 metadata buffer；
- 只有完整 external output 成功後才執行的 state commit。

同步 path 直接使用 `Stream.Read`/`Write`；async path 對 caller Stream 只使用 byte-array `ReadAsync`/`WriteAsync`。`IsoBmffWriter` 不改成逐 scalar await，避免為每個 byte 建立 async state；metadata 先在 memory 完整建立，再以 bounded bulk writes 送出。NAL length prefix 使用固定小 buffer，payload ranges 仍依既有順序輸出，async call count 以 NAL/sample 數為界，不隨 payload byte length線性放大。

各 mode 的 async I/O：

- Progressive：factory async 寫 header；audio 立即 async output；video 在 access-unit boundary/finalization async flush；finalization 以同步 Seek 配合 async mdat backpatch與 moov append。
- Faststart：沿用 progressive ingestion；finalization 保留同步 `SetLength`/position control，以固定 64 KiB buffer向後執行 `ReadAsync`/`WriteAsync` relocation，再 async 寫入 moov。
- Fragmented：factory 不輸出；第一個 media sample async 寫 initial metadata；下一個 keyframe或 finalization async 寫 moof、mdat header、video ranges與 AAC payload；所有 writes完成後才移除 selected samples、扣除 buffered bytes與推進 sequence。

不額外呼叫 `FlushAsync()`，避免改變既有 completion、durability 與 latency contract。若底層自訂 Stream 的 async override自行 fallback至同步 I/O，元件無法保證 thread reduction；元件只保證呼叫 virtual async API。

### 4. Writer 以 fail-fast operation gate 保護 suspension boundary

同一 Writer 只有一個 stateful operation可處於 Active。Canonical configure/write/finalize entry points在 state mutation前取得 non-blocking operation gate；aliases只轉送 canonical sync methods，避免重複取得。依序混用 sync/async 合法；Active期間任何其他 sync/async configure、write、finalize或 Dispose立即以 `InvalidOperationException` 拒絕，不排隊、不改變 active operation，也不推進任何 media state。

```text
Idle ── begin ──▶ Active ── success / pre-I/O rejection ──▶ Idle
  │                  │
  │                  └─ async output may have begun,
  │                     then cancel/fail ────────────────▶ Faulted
  └─ successful finalize ────────────────────────────────▶ Finalized
```

Async instance method的判斷優先序固定為：先檢查Disposed、Faulted及已Finalized的idempotent finalize，再嘗試取得operation gate；取得後先驗證argument與當前operation legality，最後才檢查already-cancelled token。據此Disposed優先為既有`ObjectDisposedException`，Faulted優先為包含「output may be incomplete」的`InvalidOperationException`，Active overlap優先為`InvalidOperationException`，null/invalid argument優先於cancellation，而已Finalized的任一finalize入口即使token已取消仍成功完成且不新增bytes。合法Idle operation的already-cancelled token在external I/O與state mutation前回傳cancelled task，instance保持可用。

Token在entry precheck後傳給每個async I/O。最後一個async I/O成功後不再執行late `ThrowIfCancellationRequested`；Writer在operation gate內同步commit並成功，因為所有physical output已完成，稍後才被signal的cancellation不能回溯已成功的檔案。Validation、format、timestamp或buffer-limit rejection若發生在本次output-risk boundary前，也保持既有可恢復性。

對已成功回傳的Writer，一旦新增async operation開始任何可能改變caller output或其後續安全重試位置的output-risk action，包括external `WriteAsync`、faststart `SetLength`、Seek/Position relocation control或backpatch reposition，之後的cancellation或任何exception都會將Writer標記為Faulted；不只限於`IOException`。Diagnostic指出output可能不完整，之後所有sync/async configure、write與finalize methods/aliases一律以`InvalidOperationException`失敗，只有無active operation時的`Dispose()`可用。這避免retry將已部分寫出的bytes重複附加。這個terminal rule只套用已回傳Writer上的新增async operation；不順便改寫既有sync I/O failure contract。

成功 finalization 後，`FinalizeFile()`、`FinalizeFileAsync()`、`Complete()` 與 `Finish()` 交叉重複呼叫都不新增 bytes。Progressive/faststart async factory若在header output中途取消或失敗，task呈現cancellation/failure、不回傳Writer、output可能已部分寫入，且不因`leaveOpen:false`主動關閉caller Stream；沒有caller可觀察的Faulted instance，caller必須丟棄或自行恢復該output。

### 5. Tests 先以 deterministic async-only Streams 定義 contract

Unit tests新增可精確控制的 Streams：

- sync `Read`/`Write` 直接失敗、async override以 `TaskCompletionSource` gate完成，用來證明產品沒有 `Task.Run` 或直接同步 external payload I/O；
- partial-read、throw-after-byte、cancel-at-phase 與 non-seekable async output；
- call counters、bytes、token observation、maximum outstanding operation 與 deterministic phase markers。

測試不得依賴 `Thread.Sleep`、GC timing或偶然 ThreadPool排程。案例涵蓋 Reader position restore/ownership/resource/error parity；Writer pre-cancel、mid-write/mid-relocation cancellation與 exception、Faulted rejection、overlap rejection且第一個 operation仍可完成、sequential mixed calls、leave-open與 cross-sync/async finalization idempotency。

Fixed H.264/H.265 × progressive/faststart/fragmented fixtures需證明 pure async與依序 mixed output都和既有同步 SHA-256、box/payload摘要及 Reader round-trip一致。Integration tests使用實際 async file-backed Stream完成六種 codec/layout、public async Reader snapshot、`ffprobe`與`ffmpeg -v error`；fragmented另驗證 async-only non-seekable output。

### 6. Console 以第四參數選擇 I/O path

`samples/DotCore.Mp4.Console` 改為 `static async Task<int> Main`，CLI 擴充為：

```text
DotCore.Mp4.Console <output-path>
  [progressive|faststart|fragmented]
  [h264|h265]
  [sync|async]
```

省略第四參數仍為 `sync`，所以既有 0–3 argument invocation、progressive/H.264 defaults及output file行為不變；stdout是additive compatibility，會新增`I/O: sync`一行而不是byte-for-byte不變。Async branch必須 await Writer factory、canonical async write/finalize methods與 Reader factory；snapshot完成後仍使用同步 `Read()` 觸發 events。其餘既有 mode、codec、parsed configuration與 3 video/4 AAC timed lines保持存在。Console smoke tests覆蓋 explicit async的 H.264/H.265 × 三種 layout、預設 sync compatibility及未知 I/O mode的 nonzero/no-success-output行為。

### 7. Benchmark 分離 overhead、real I/O 與 scalability

沿用現有 Release capture/compare/provenance/self-test harness，scenario identity新增 `IoMode`、`StreamKind`、`Concurrency` 與 async delay/gate identity；結果新增 async read/write calls、sync fallback calls、bytes、maximum outstanding I/O、completed operations、synchronously completed operations/ratio、elapsed/throughput、allocated bytes、GC與 diagnostic ThreadPool aggregate observations。結果只記錄fixture identity、logical byte counts與hash，不記錄raw media payload、remote URI、token/credential、任意Stream識別或process-wide敏感資料；command與path使用受控值、repo-relative表示或redaction。

Async benchmark dispatcher MUST await measured Task完成後才停止timestamp、擷取allocation/GC/calls與進行cleanup；concurrency scenario MUST await `Task.WhenAll`。Dispatcher在await前觀察Task completion state以計算synchronous completion ratio，避免只量到Task建立時間。

三個 family分開解讀：

1. **Immediate-completion overhead**：pre-sized MemoryStream/counting Stream paired sync/async Reader snapshot、progressive ingestion、fragment flush與 faststart finalization，回報 ns/op、ops/s與 allocation；不得據此宣稱 async improves throughput。
2. **Real file I/O**：以`FileOptions.Asynchronous`建立temporary FileStream並使用至少 1 MiB deterministic payload，量測 Reader snapshot及三種 Writer layout；setup、file creation與cleanup排除於 measured operation。這證明library選用async API，但仍不宣稱每個平台一定suspend。
3. **Bounded-concurrency scalability**：至少一個Reader snapshot與一個代表性Writer layout分別在async-only gated/delayed Stream以 concurrency 1、32、128同時 pending，證明全部完成、maximum in-flight符合 scenario、sync external call count為零且無 deadlock。ThreadPool count/queue變化只作aggregate診斷，不作環境敏感 hard gate。

Pre-change與candidate仍在相同環境執行至少三個獨立 process，以 exact scenario identity及 median比較。既有 sync required scenarios套用不退化超過10%的 gate；async overhead與real-I/O數值先誠實回報，不設定宣稱普遍加速的任意門檻。Comparator/self-test必須拒絕遺漏 async identity、不同 concurrency/stream kind、sync fallback非零或不完整 operations。

### 8. Compatibility baseline 與 README 都是交付物

Approved public API baseline只接受明列的 additive members；既有 public members、target framework與production package set不得 drift。Fixed-output baseline不更新 hashes，因 sync、async及mixed paths必須 byte-identical。

`README.md` 同時提供同步與 async完整範例、Reader factory範圍、Writer async factory/write/finalize、sequential mixing與overlap規則、cancellation/Faulted語意、leave-open/capability、必要同步 Seek/SetLength、無 implicit flush、底層 Stream async品質限制、Console第四參數、benchmark commands/metrics及「scalability不等於單次速度提升」說明。標記的C# usage fence由automated doc-snippet test抽出並對current library編譯，避免README與public API漂移。

## Risks / Trade-offs

- [Async suspension讓 Writer state更容易被重入] → 使用單一 fail-fast operation gate與 gated concurrency tests，不以 `SemaphoreSlim` 排隊模糊 submission order。
- [取消或 I/O failure留下 physical partial output] → output開始後將 Writer標記 Faulted、拒絕 retry，文件化 temporary-file/rename交付策略。
- [逐 byte或tiny write async化造成大量 Tasks與 throughput退化] → metadata先在 MemoryStream同步建立，external Stream只做 bounded bulk async calls，並記錄call counts與sync regression gate。
- [底層 Stream的 async override可能同步 fallback] → 明確限制承諾、以 async-only test Stream證明library dispatch，benchmark分開 Stream kind。
- [Reader parsing與 Seek/SetLength仍占用 thread] → 文件化 async只涵蓋I/O wait，不使用 `Task.Run`偽裝CPU/control operations。
- [Public API baseline intentional drift掩蓋其他breaking change] → comparator只核准列出的新增 signatures，既有 signatures及packages逐項保持。
- [Console新增參數破壞既有自動化] → 第四參數optional且預設sync，保留原0–3 argument與輸出內容，只新增一行I/O標示。
- [Sync/async state machine複製後逐漸分歧] → review要求共用validation/planning/state commit，只保留兩個明確sync/async external-I/O leaves，禁止整份Writer state machine複製。

## Migration Plan

1. 先提交 failing API/structural tests、async-only Stream fixtures與 pre-change sync compatibility/performance baseline。
2. 以 Reader async factory完成第一個獨立 slice，再加入 Writer operation state與 async factory。
3. 依 progressive、fragmented、faststart順序接通 async I/O，每一 slice完成 targeted tests及 fixed-output comparison。
4. 擴充 integration matrix、Console async mode、benchmark harness與 README。
5. 執行 public API/package comparator、sync/async fixed-output、unit/integration/full solution、Console、FFmpeg及三次 Release benchmark comparison。
6. Rollback時移除 additive async surface及其internal helpers，恢復Console/benchmark/docs的sync-only內容；既有同步路徑與MP4格式不需資料migration。

## Open Questions

- 無阻擋實作的未決問題。Async benchmark不預設普遍 throughput改善門檻；先以真實量測呈現各 Stream kind的成本與scalability，再由未來獨立change決定是否需要額外效能門檻。

## Context

`Mp4Reader` 目前在 constructor 將完整輸入 snapshot 到單一 `byte[]`，解析時再以 `Slice` 為每個 NAL/AAC payload 配置 array，最後交由 public sample constructor 再複製一次。Event args 與 public `Data` getter 的額外 defensive copy 是公開隔離語意的一部分，但 `Slice` 與 sample constructor 之間的第二次 ownership copy 並不是。

`Mp4Writer` 收到的 `EncodedVideoNalUnit` 已擁有 caller payload 的 defensive copy；`NalUnits.Normalize` 仍會為 raw NAL 或每個 Annex-B NAL 再建立 arrays。Fragmented mode 又會先建立完整 length-prefixed access-unit payload，並為了計算 `trun.data_offset` 建立 provisional 與 final 兩份 `moof`。這些成本會隨 bitrate、NAL 大小與 GOP 長度增加。

現有 repository 沒有 performance harness。Production library 是 dependency-free `netstandard2.0`，公開 contracts 以獨立 `byte[]` 保護 caller mutation，Reader 保證 constructor-time snapshot 與可重複列舉，Writer 支援 progressive、faststart、fragmented 三種 layout。這些都是本次最佳化不可改變的 constraints。

## Goals / Non-Goals

**Goals:**

- 先以固定 fixtures 建立 Reader/Writer allocation、throughput、GC 與 Stream call-count baseline，再執行最佳化並以相同 harness 比較。
- 將 Reader 每個 emitted payload 在 public caller 明確要求 copy 前的內部 payload-sized allocation，從兩份降為至多一份。
- 讓 Writer 以指向已擁有 sample buffer 的 ranges 處理 raw 與 Annex-B NAL，並讓 fragmented output 不再保存完整的第二份 length-prefixed access-unit payload。
- 以單一 finalized `moof` backing buffer 解決 `trun.data_offset`，避免 provisional/final 完整重建。
- 維持 public API、defensive-copy、snapshot、event ordering、timestamps、exceptions、resource guards、stream ownership 與 MP4 bytes。

**Non-Goals:**

- 不新增或修改 async API；不以 `Task.Run` 包裝同步 I/O。
- 不新增 public `ReadOnlyMemory<byte>`、`Span<byte>`、borrowed-memory 或 ownership-transfer API。
- 不將 Reader 改成 tail-follow、on-demand seek/read 或 single-pass streaming。
- 不移除 caller-visible `Data`、event args 或 codec configuration defensive copies。
- 不重新設計 faststart layout、預留 `moov` 空間，或試圖消除 faststart 必要的整段 `mdat` 搬移。
- 不對無關 parser、box model、命名或格式進行重構。

## Decisions

### 1. Benchmark 與 allocation tripwire 先於最佳化

新增獨立 `benchmarks/DotCore.Mp4.Benchmarks` .NET 10 project，benchmark dependency 僅存在於該 project，不傳遞至 production library。固定 fixtures 在 measured operation 外建立；Reader 分開量測 constructor snapshot、無 event delivery、有 event delivery與 caller 明確讀取 `Data`，Writer 分開量測 progressive ingestion、fragmented GOP flush 與 faststart finalization。

每個案例記錄 runtime、OS、CPU、fixture 大小、sample/NAL 數、GOP、Stream 類型、allocated bytes、GC、throughput 與同步 Stream call count。`allocated bytes` 統一定義為 fixture、sample 與 output-buffer setup 完成後，一次 measured operation 由 benchmark harness 回報的 total managed allocated bytes；每個結果同時記錄 operation count 與 logical payload bytes。Baseline artifact 另記錄 source commit SHA、dirty-worktree state、benchmark command、harness version、完整 scenario/parameter identity、result path 及 result SHA-256。

Required acceptance IDs 固定為 Reader large video single/multi-NAL no-event delivery、Reader large AAC no-event delivery、Writer progressive raw/Annex-B single/multi-NAL ingestion、faststart Annex-B ingestion、fragmented short/long-GOP flush 及 faststart finalization；allocation 降低 35% 的門檻只套用 Reader delivery、progressive/faststart ingestion 與 fragmented flush，所有 required IDs 均套用 throughput 不退化超過 10% 的門檻。Reader delivery 在 Reader 已建立後量測，Writer 使用 pre-sized output 或 counting Stream 並將 output growth 分開回報。

Baseline 與 candidate 使用同一 checkout 環境、相同 Release command 及至少三次獨立 process run，以 median 比較。Machine-readable comparator 必須拒絕遺漏或不一致的 scenario identity、回報兩側 result paths，並在 35%/10% 門檻不符時以非零 exit code 結束。Allocation acceptance 另外由 deterministic unit-level structural tests 保護，避免只依賴有雜訊的 wall-clock benchmark。

替代方案是只加入 `GC.GetAllocatedBytesForCurrentThread` unit tests；它較容易成為環境敏感門檻，也無法提供吞吐、GC 與 call-count脈絡，因此只作為窄範圍 regression tripwire，不取代完整 benchmark。

### 2. Reader 使用 internal named ownership-transfer factory

Public `EncodedVideoNalUnit`、`EncodedAudioSample` constructors 繼續 defensive-copy。Reader 的 `Slice` 仍配置一份只包含 payload 的 owned array，再透過 assembly-internal named factory 將該 array直接交給 sample，不進行第二次複製。

不讓 sample 直接保存完整 Reader snapshot 的 offset view，因為 caller 只保留一個小 sample 時可能意外延長最多 256 MiB snapshot 的 lifetime。Owned payload array 讓 sample 在 Reader dispose 後仍可安全使用，也維持目前的 retention profile。

Factory 必須是 internal 且名稱明確表達 ownership transfer；不得用容易誤用的 public overload或布林 `skipCopy`。Public constructors 與 internal factory 共用 null、empty、PTS、DTS、duration 及 keyframe 相關 validation，只有 payload copy 步驟不同，並保持 exception type、`ParamName` 與語意相等的 actionable diagnostic。Event args 仍從 `sample.Data` 取得獨立 array，public `.Data` 每次仍回傳 defensive copy。

### 3. Writer normalization 回傳 immutable ranges

新增不為每個 NAL 配置 object 的 internal readonly NAL range value，包含 backing `byte[]`、checked offset 與 count。`NalUnits.Normalize` 僅掃描 start codes、驗證空 NAL 與邊界，並回傳指向來源 buffer 的 ranges；raw NAL 是涵蓋完整來源的單一 range。Writer 使用 `EncodedVideoNalUnit.DataBytes`，因此 backing array 已由 sample constructor 擁有且 caller 無法透過正常 public API 修改。大量 tiny-NAL fixture 用來防止 range object explosion 與 unbounded Stream call amplification。

Pending access unit 保存 ranges，progressive/faststart flush 直接寫 length prefix 與 range；同 PTS/DTS aggregation、NAL order、duration與 keyframe validation 維持不變。Codec parameter-set validation 只在方法內同步檢查 ranges，不把 caller array保存到 configuration。

替代方案是加入 public borrowed-memory sample type，可移除最初 defensive copy，但會引入 caller lifetime、mutation 與 `netstandard2.0` dependency問題，超出本 change。

### 4. Fragment sample 保存 payload source，而非強制單一 payload array

Fragment sample representation 同時支援 contiguous AAC payload 與由多個 NAL ranges 組成的 video payload，並保存預先 checked 的 logical encoded size。Fragment buffer accounting 維持既有語意，只計實際輸出的 length-prefix 加 NAL/AAC payload bytes；Annex-B backing array 中的 start-code bytes 屬於 physical retained bytes，但不得計入 `MaximumFragmentBufferBytes`。Flush 時仍依既有 video-first、audio-second layout，直接依 ranges 寫出內容。

Writer 必須持有 ranges/backing arrays 直到 fragment 成功寫出，才能保持 caller mutation isolation。只有完整 fragment 成功寫出後才能移除 selected samples、扣除 logical buffered bytes 並釋放 references；`moof`、`mdat` header 或 mid-range payload write 失敗時，不得提前移除或扣除 internal state。測試以 test-visible collection/accounting 檢查為主，不以 `WeakReference`/GC timing 作唯一證據。不使用 `ArrayPool<byte>` 保存跨呼叫 payload，避免 pool lifetime、資料清除與 failure 後歸還問題。

Range writes 可能增加 `Stream.Write` call count。Counting Stream 要求 call count 不隨 payload byte length 成長，並記錄每個 NAL 的 bounded header/payload writes；call count 作為診斷，正式拒絕條件是 required scenarios 的 10% throughput gate。若退化超標，實作應以小型 header batching 或現有 Stream buffering 處理，而不得恢復完整 payload materialization。

### 5. `moof` 在單一 buffer 內 backpatch data offsets

Movie-fragment builder 一次邏輯寫入 `moof`，同時記錄每個 `trun.data_offset` 的四位元組位置。MemoryStream 以 sample/track counts 計算的 checked conservative upper-bound capacity 預先配置，避免常態成長 copy，但契約重點是不得再建立 provisional/final 兩份完整 `moof`。完成 buffer 後以實際 `moof` length、`mdat` header 與 video payload size 計算 offsets，使用 big-endian checked write 原地 backpatch。Patch 不改變 box 長度，因此不需要 provisional build。

Builder 回傳 internal buffer segment（backing array 加有效長度），Writer 只寫有效範圍。Buffer 不公開給 caller；所有 offset、32-bit range與 metadata-length invariants 仍以既有 exception semantics fail loudly。

替代方案是先以公式預算完整 `moof` 大小；雖可行，但會把 box field/flag規則複製到另一套 size calculator，日後較容易與實際 writer drift。

### 6. 相容性以輸出與 API 雙重基線保護

固定 H.264/H.265、single/multi-NAL、Annex-B/raw、AAC 與三種 layout fixtures 在最佳化前保存 SHA-256 與結構摘要。Candidate 必須產生 byte-identical output；Reader payload、PTS、DTS、duration、keyframe、events，以及 invalid cases 的 exception types、rejection timing 與語意相等 actionable diagnostics 全數保留。

Public surface 由 reflection-based approved API snapshot 或 repository 可用的 ApiCompat 檢查比較，production package不得新增 public member或 dependency。Integration matrix 仍以 public Reader、`ffprobe` 與 `ffmpeg -v error` 驗證，避免只證明 internal self-consistency。

## Risks / Trade-offs

- [Internal ownership factory 被錯誤使用而接收可變 caller array] → factory 僅供 Reader 的新配置 payload呼叫，命名表達 transfer，並以 mutation-isolation tests 證明 public路徑仍複製。
- [Sample 改為 snapshot view 導致大型 array retention] → 明確保留每 payload 一份 owned array，不採 snapshot slice view。
- [NAL ranges 生命週期不足或被修改] → ranges 只指向 `EncodedVideoNalUnit` 的 private owned buffer，Writer 持有 backing array直到完成 flush。
- [Fragment range writes 增加 virtual calls、降低吞吐] → 記錄 Stream call count，要求 call count 不隨 payload byte length 成長，並對 required scenarios 以三次 process median 驗證 10% throughput 門檻；必要時只 batching 小型 headers。
- [`moof` patch 位置或 offset 計算錯誤] → 先加入多軌、空 audio、signed composition offset、multiple samples與邊界 tests，再要求 byte-identical output和外部 decode。
- [Allocation benchmark 受 JIT、GC 或 fixture setup 污染] → 使用 Release、warmup、setup outside measured operation、固定 fixtures和三次 process median；以 structural unit tests作 deterministic guard。
- [最佳化擴張至 public memory API或 async] → tasks與scope review明確排除，後續另建 OpenSpec change。

## Migration Plan

1. 加入 benchmark harness、approved public API snapshot、固定 output hashes與現有 allocation baseline，不修改 production behavior。
2. 先實作 Reader internal ownership transfer並完成 targeted/all-reader驗證。
3. 再實作 Writer NAL ranges與fragment payload source，逐一驗證 progressive、faststart、fragmented bytes。
4. 最後改為單一 `moof` buffer backpatch，重跑完整 unit/integration/Console/FFmpeg 與 candidate benchmarks。
5. 若任一階段 correctness、allocation target或throughput門檻失敗，回復該獨立 internal slice；沒有 schema、資料或 public API migration。

Rollback 只需 revert internal最佳化與相關 benchmark expectations；既有同步 public API與檔案格式不需 consumer migration。

## Open Questions

- 無阻擋 implementation 的 open question。Async I/O與public memory view的 API/TFM決策保留給後續 change。

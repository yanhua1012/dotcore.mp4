## 1. Baseline、API contract 與 deterministic async test infrastructure

- [ ] 1.1 將三份delta specs的Reader、Writer、Console、integration、benchmark及README scenarios整理成test matrix，標記unit/integration/manual/benchmark證據與每個case的expected sync/async Stream calls。
- [ ] 1.2 在修改production前執行tracked public API/package baseline與fixed-output self-test，保存source commit、dirty state、commands、result paths及hash。
- [ ] 1.3 在修改production前以現有Release harness對required sync scenarios完成至少三次獨立process capture，保存scenario medians作為sync regression baseline。
- [x] 1.4 在unit-test project加入可重用的async-only gated Stream，讓sync Read/Write fail、async overrides由`TaskCompletionSource`控制，並記錄tokens、calls、bytes與maximum outstanding I/O。
- [x] 1.5 加入partial-read、throw-after-byte、cancel-at-phase、seekable file-like與non-seekable async output fixtures；所有phase由deterministic gates控制，不使用`Thread.Sleep`。
- [x] 1.6 加入failing structural/public API tests，精確要求核准的Reader/Writer `Task` signatures、optional `CancellationToken`、正體中文 XML documentation、`netstandard2.0` target及zero production package drift。
- [ ] 1.7 執行API/fixture targeted tests，保存新public members尚未存在且async-only Streams尚未被production使用的red baseline。

## 2. Reader async snapshot slice

- [x] 2.1 加入failing Reader tests，涵蓋async-only delayed snapshot、nonzero position restore、partial reads、configuration/payload/event parity及post-construction zero Stream I/O。
- [x] 2.2 加入failing Reader cancellation/ownership tests，涵蓋pre-cancel、mid-read cancel、truncated/oversize/capability/parser failure、finally restore attempt、read/cancel加restore雙重失敗優先序、`leaveOpen:false` factory failure不關閉input及成功Dispose ownership。
- [x] 2.3 執行Reader targeted tests，確認failure只來自`CreateAsync`/async snapshot尚未實作並保存red evidence。
- [x] 2.4 重構constructor初始化，使sync/async paths共用capability/length guards、track parsing及instance initialization，且不改變既有sync exception與snapshot語意。
- [x] 2.5 實作具正體中文 XML documentation 的`Mp4Reader.CreateAsync`與byte-array `ReadAsync` loop，使用`ConfigureAwait(false)`、傳遞token並在`finally`恢復position。
- [x] 2.6 執行Reader async、ownership、resource-limit、malformed-input、event-order及repeatable-enumeration tests至green，並確認沒有新增async enumeration或`Task.Run`。

## 3. Writer operation state 與 async factory slice

- [x] 3.1 加入failing Writer factory tests，涵蓋兩個overloads、options snapshot、progressive/faststart async header、fragmented zero-output lazy start、invalid capability/options zero-success-header、mid-header cancel/exception的partial output/no returned instance及failure ownership。
- [x] 3.2 加入failing operation-gate tests，以delayed active operation驗證async/async與sync/async configure/write/finalize/dispose overlap fail-fast、active operation仍完成且rejected call不改state。
- [x] 3.3 加入failing cancellation/fault-state tests，區分already-cancelled與pre-output validation可恢復、已回傳Writer跨越output-risk boundary後cancel/任意exception terminal Faulted、Faulted後methods/aliases的`InvalidOperationException`及僅Dispose可用。
- [ ] 3.4 加入failing precedence matrix，涵蓋cancelled token搭配disposed/Faulted/Active/Finalized instance、null sample及合法Idle operation，並加入final I/O成功後late cancellation仍commit成功的deterministic gate。
- [x] 3.5 執行Writer factory/state targeted tests，保存async factory、operation gate、precedence及terminal state尚未存在的red baseline。
- [x] 3.6 實作non-blocking canonical operation guard與Idle/Active/Finalized/Faulted transitions，讓aliases只轉送canonical methods，並保持sequential sync/async mixing合法。
- [x] 3.7 分離Writer capability/options/state初始化與header planning，實作兩個具正體中文 XML documentation 的`CreateAsync` overload；fragmented保持lazy start。
- [x] 3.8 實作state/argument/gate/cancellation precedence、pre-output rejection recovery、output-risk fault transition、late-cancel commit cutoff與fault diagnostics，且不改變既有sync I/O failure contract。
- [x] 3.9 執行factory、operation、ownership、mode contract及既有constructor tests至green，確認sync path不等待Task且overlap tests無timing dependency。

## 4. Progressive async ingestion 與 finalization slice

- [x] 4.1 加入failing progressive tests，要求async audio immediate output、pending video access-unit flush、multi-NAL order、mdat backpatch及moov append只使用caller Stream async writes。
- [ ] 4.2 加入failing progressive cancel/failure tests，分別注入mid-audio、mid-video length/payload、mid-backpatch及mid-moov，assert partial bytes、no false commit與terminal Faulted。
- [x] 4.3 加入failing pure-async及sequential mixed H.264/H.265/AAC fixtures，要求與sync progressive SHA-256、box/payload摘要及Reader round-trip完全相同。
- [x] 4.4 執行progressive targeted tests，保存canonical async write/finalize path尚未貫穿external I/O的red baseline。
- [x] 4.5 實作具正體中文 XML documentation 的`WriteVideoNalUnitAsync`與`WriteAudioSampleAsync`，共用validation/planning並以bounded prefix/payload `WriteAsync`完成progressive ingestion。
- [x] 4.6 實作`FinalizeFileAsync`的pending-video flush、同步seek control、async mdat backpatch與moov append，不新增implicit `FlushAsync`。
- [ ] 4.7 執行progressive async、mixed、cancellation、failure、timestamp、aggregation及idempotent cross-sync/async finalization tests至green。

## 5. Fragmented async output slice

- [x] 5.1 加入failing fragmented tests，要求first media以async writes輸出initial metadata，下一keyframe與finalization以async writes輸出keyframe-aligned `moof`/`mdat`及video/AAC payload。
- [x] 5.2 加入failing async non-seekable Stream tests，涵蓋H.264/H.265、short/long GOP、mixed AAC、exact buffer boundary及pure async/mixed byte-identical round-trip。
- [ ] 5.3 加入failing cancel/exception matrix，分別在initial metadata、moof、mdat header與mid video/AAC payload中斷，assert selected samples/accounting/sequence不提前commit且Writer terminal Faulted。
- [x] 5.4 執行fragmented targeted tests，保存startup/fragment flush仍呼叫同步external writes的red baseline。
- [x] 5.5 將initial metadata building與external output分離，實作`EnsureFragmentedStartedAsync`並只在完整async output後標記started。
- [x] 5.6 實作`FlushFragmentAsync`與fragment payload async range writes，維持video-first/audio-second order、bounded call model及成功後state commit。
- [x] 5.7 將async video/audio ingestion接至fragment selection/flush而不複製既有state logic，保留first-keyframe、global DTS與buffer-limit rejection timing。
- [x] 5.8 執行fragmented async、non-seekable、output failure、resource、ordering、state accounting及sync regression tests至green。

## 6. Faststart async relocation slice

- [ ] 6.1 加入failing faststart tests，要求fixed 64 KiB buffer、同步SetLength/position controls、async-only relocation Read/Write、moov placement及chunk offsets。
- [ ] 6.2 加入failing relocation cancellation/failure cases，涵蓋SetLength、Seek/Position、mid-read、partial async read、mid-shift write與final moov write的任意exception，assert diagnostics、partial-output warning及terminal Faulted。
- [ ] 6.3 加入failing H.264/H.265/AAC pure async及sequential mixed faststart comparison，要求sync SHA-256、box/payload摘要與Reader round-trip parity。
- [ ] 6.4 執行faststart targeted tests，保存relocation仍使用同步caller Stream Read/Write的red baseline。
- [ ] 6.5 實作cancellable `RelocateMdatForFastStartAsync`，以`ReadAsync`填滿每個backward chunk、`WriteAsync`搬移並保持checked offsets與bounded memory。
- [ ] 6.6 將`FinalizeFileAsync` faststart branch接至async relocation與final metadata output，維持既有`stco`/`co64` stable-layout logic。
- [ ] 6.7 執行faststart async、failure、fixed-offset、idempotency及sync regression tests至green。

## 7. Cross-layout compatibility 與 integration verification

- [ ] 7.1 擴充fixed-output generator/tests，對H.264/H.265 × progressive/faststart/fragmented執行sync、pure async及sequential mixed paths，先保存未支援async matrix的failing result。
- [ ] 7.2 更新approved compatibility baseline，只核准明列async signatures與XML docs；既有members、`netstandard2.0` target及production package set不得改變，fixed-output hashes不得更新。
- [ ] 7.3 在integration fixtures以`FileOptions.Asynchronous`加入actual async file-backed Stream write/read helpers，使用public Writer async factory/write/finalize與Reader async factory。
- [ ] 7.4 擴充H.264/AAC及H.265/AAC三種layout integration matrix，assert codec、payload、PTS/DTS/duration、keyframe、event order及sync output parity。
- [ ] 7.5 對六種async codec/layout outputs執行PATH-resolved `ffprobe`及`ffmpeg -v error`，工具缺少時明確回報未完成而不宣稱通過。
- [ ] 7.6 執行unit與integration projects，確認兩者皆有非零discovered/passed count、無silent skip，且所有cross-layout comparisons green。

## 8. Console async demonstration

- [x] 8.1 擴充Console smoke tests，先要求第四參數`async`的H.264/H.265 × 三種layout、`I/O: async`、3 video/4 AAC lines與public Reader round-trip，保存CLI尚未支援的red baseline。
- [x] 8.2 加入default/explicit `sync` compatibility與unknown I/O mode nonzero/no-success-output tests，assert既有stdout lines仍存在但允許additive `I/O: sync`，並維持既有arguments/defaults/file output與unknown mode/codec behavior。
- [x] 8.3 將Console entry point改為`async Task<int> Main`，新增optional `[sync|async]` parsing與`I/O:`輸出，省略時維持sync。
- [x] 8.4 實作async branch，await Writer factory、canonical sample/finalization methods與Reader factory；snapshot後仍以既有同步Reader delivery觸發events。
- [x] 8.5 執行Console smoke tests與代表性六種CLI commands，確認existing invocation未破壞且outputs可由Reader、`ffprobe`與`ffmpeg`驗證。

## 9. Async benchmark、scalability 與 comparator

- [ ] 9.1 加入failing benchmark model/self-tests，要求scenario identity含`IoMode`、`StreamKind`、`Concurrency`與delay/gate，result含sync/async calls/bytes、max outstanding、completed/synchronously-completed operations/ratio及既有provenance。
- [ ] 9.2 擴充counting/instrumented benchmark Streams與result serialization，redact或約束commands/paths且不記錄payload、URI、credential或任意Stream identity，保持fixture/setup/output growth在measured operation外並確保temp files deterministic cleanup。
- [ ] 9.3 實作paired immediate-completion memory scenarios，分開量測sync/async Reader snapshot、progressive ingestion、fragment flush及faststart finalization的throughput/allocation/GC/calls。
- [ ] 9.4 以`FileOptions.Asynchronous`實作至少1 MiB deterministic payload的temporary file scenarios，涵蓋Reader snapshot及三種Writer layout並明確標記file-backed Stream。
- [ ] 9.5 實作async benchmark dispatcher，確保在停止計時、擷取allocation/GC/calls及cleanup前await measured Task，並在await前記錄synchronous completion。
- [ ] 9.6 實作async-only gated/delayed Stream concurrency 1/32/128 Reader snapshot及代表性Writer scenarios，以`Task.WhenAll`等待並回報maximum in-flight、completed operations及aggregate ThreadPool observations且不得deadlock。
- [ ] 9.7 擴充comparator/self-test，對missing/mismatched async identity、Stream kind、concurrency/delay、nonzero sync fallback、incomplete operations或provenance/hash以nonzero失敗。
- [ ] 9.8 執行benchmark self-test與每個family的short smoke capture，確認完整Task已納入timed region、scenario identity、nonzero operations、call counters及cleanup正確。
- [ ] 9.9 以相同Release環境完成至少三次candidate process capture，比較pre-change sync medians並證明所有既有required sync IDs throughput未退化超過10%。
- [ ] 9.10 產生async memory/file/concurrency evidence report，誠實記錄overhead、synchronous completion ratio與scalability，不將任一Stream結果外推為普遍單次throughput提升。

## 10. README、完整驗證與交付審查

- [x] 10.1 更新`README.md`，加入具固定markers的sync/async完整C#範例、Reader snapshot-only範圍、Writer async factory/write/finalize及sequential mixing/overlap規則。
- [x] 10.2 加入automated doc-snippet test，從README markers抽出C# usage並對current library編譯，先保存missing/stale snippet會red的證據再驗至green。
- [x] 10.3 文件化pre-cancel、state/argument precedence、factory partial header、output-risk terminal Faulted、leave-open/capability、必要同步Seek/SetLength、無implicit FlushAsync、temporary-file/rename及底層Stream async fallback限制。
- [ ] 10.4 文件化Console第四參數、代表性sync/async commands、benchmark三個families、capture/compare/self-test commands、metrics及正確效能解讀。
- [ ] 10.5 執行solution restore、build及solution-level test，要求unit/integration皆有非零passed count、零unexpected skip、零warning/error。
- [ ] 10.6 執行approved API/package、fixed-output、all Reader entry points、stream ownership、resource/error、output failure及三種layout regression suites。
- [ ] 10.7 執行Console H.264/H.265 sync/async代表矩陣、`ffprobe`、`ffmpeg -v error`及benchmark Release comparator，保存commands、outputs、result paths與hash。
- [ ] 10.8 完成correctness、security/privacy、performance/complexity及scope reviews，確認無payload/credential logging、unbounded Task/I/O、sync-over-async、deadlock、dependency或public alias scope creep，且validation/planning/state commit共用、只保留兩個external-I/O leaves而未複製整份state machine。
- [ ] 10.9 執行OpenSpec strict validation、artifact/requirement/scenario/task機器計數及`git diff --check`，將observed counts、tests、benchmark medians、風險與rollback記錄至`tasks/todo.md` Results。

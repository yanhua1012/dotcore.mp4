# MP4 Component Verification Specification

## Purpose

Define the project structure, interoperability checks, and executable demonstration required to verify the MP4 component.

## Requirements

### Requirement: Test-first project structure
Solution SHALL 維持 .NET Standard 2.0 production library、獨立 xUnit unit-test project、獨立 xUnit integration-test project 與 .NET 10 Console demonstration project。兩個 test projects MUST 明確宣告可由標準 `dotnet test` discovery 執行；任何 behavior 的 tests MUST 在其 production implementation task 完成前加入，且完成驗證 MUST 回報非零 discovered/executed test count。

#### Scenario: Run unit tests independently
- **WHEN** developer 執行 unit-test project
- **THEN** tests MUST 在不呼叫 ffmpeg 或 ffprobe 的情況下驗證 box encoding、codec configuration、NAL aggregation/splitting、timestamp、progressive tables、faststart relocation、fragment construction/parsing、resource guards 與 error paths

#### Scenario: Prevent a zero-test false green
- **WHEN** developer 以文件化的 solution-level `dotnet test` command 執行驗證
- **THEN** command MUST 實際 discover 並執行兩個 test projects 的非零測試，且零測試或空白 test output MUST NOT 被記錄為成功

### Requirement: MP4 interoperability integration verification
Integration-test project SHALL 透過 public writer API 為 H.264/AAC 與 H.265/AAC 產生 progressive、faststart、fragmented MP4，透過 public reader API 重新解析，並比較 codec configuration、media payload、PTS、DTS、duration 與 keyframe 資訊。它 MUST 以 PATH-resolved `ffprobe` 與 `ffmpeg` 驗證每種 generated layout，並 MUST 以 FFmpeg 產生的 common fragmented MP4 驗證 reader，而不得僅依賴本元件 writer/reader 自我一致。

#### Scenario: Validate every generated layout with external tools
- **WHEN** integration tests 完成 H.264/AAC 或 H.265/AAC 的任一 writer mode 且 `ffprobe` 與 `ffmpeg` 可從 PATH 取得
- **THEN** `ffprobe` MUST 辨識預期 container、codec streams 與 fragmented state，且 `ffmpeg` MUST 無 error-level diagnostics 地完成 decode validation

#### Scenario: Read an independently generated fragmented MP4
- **WHEN** FFmpeg 產生含固定 GOP、initial moov 與多個 keyframe-aligned fragments 的 H.264/AAC 或 H.265/AAC MP4
- **THEN** public `Mp4Reader` MUST 還原預期 codec、payload、timing 與 keyframe boundaries

#### Scenario: Report missing external verification tools
- **WHEN** ffprobe 或 ffmpeg 無法從 PATH 取得
- **THEN** integration test MUST 回報清楚命名缺少 executable 的 skipped 或 failed prerequisite，且 MUST NOT 宣稱 interoperability verification passed

### Requirement: Console round-trip demonstration
.NET 10 Console project SHALL 保留 output MP4 path 為第一個 positional argument，接受第二個可選的 `progressive`、`faststart` 或 `fragmented` mode，省略時 SHALL 使用 progressive；第三個可選 codec SHALL 接受 `h264` 或 `h265`，省略時 SHALL 使用 H.264。它 MUST 使用含 keyframe、non-keyframe 與下一個 keyframe 的固定合法 H.264/H.265/AAC samples，以所選 mode 與 codec 建立並完成 MP4、重新開啟該檔、訂閱 video-NAL 與 AAC events，並印出 selected mode、parsed codec parameters、每個 emitted sample 的 timestamps 與 video keyframe state。

#### Scenario: Run each demonstration mode
- **WHEN** developer 分別以 H.264 與 H.265 執行 progressive、faststart 與 fragmented Console mode
- **THEN** 每次執行 MUST 在指定 path 寫出該 codec/layout 的完整 MP4，透過 public `Mp4Reader` 讀回，並印出 mode、codec、parsed parameters 與所有預期 video/AAC samples

#### Scenario: Preserve existing Console invocation
- **WHEN** developer 只提供既有的 output path argument
- **THEN** Console MUST 產生 progressive MP4，並維持成功 round-trip 行為

#### Scenario: Reject an unknown Console mode
- **WHEN** developer 提供不是 progressive、faststart 或 fragmented 的 mode
- **THEN** Console MUST 顯示可操作的 usage、回傳非零 exit code，且 MUST NOT 留下被宣稱成功的 output

### Requirement: Layout-specific structural verification
Verification suite SHALL 使用至少含兩個 keyframes 且中間含 non-keyframe 的固定合法 GOP，直接檢查三種 top-level box order、faststart chunk offsets、fragment count、keyframe boundaries、track defaults、decode times、sample flags、composition offsets 與 `mdat` ranges。Fixtures MUST 固定且可重現，不得依賴隨機 timing 或未記錄的 encoder 狀態。

#### Scenario: Prove faststart placement and offsets
- **WHEN** tests 完成 faststart fixture
- **THEN** assertions MUST 證明 `moov` 位於 `mdat` 前、所有 sample offsets 指向預期 payload，且 reader round-trip 與外部 decode 均通過

#### Scenario: Prove keyframe fragment boundaries
- **WHEN** tests 寫入 keyframe、non-keyframe、下一個 keyframe 的 fragmented fixture
- **THEN** assertions MUST 證明第二個 keyframe 開始新的 fragment、non-keyframes 留在前一 GOP，且 reader 回報相同 keyframe state

#### Scenario: Verify bounded and malformed paths
- **WHEN** unit tests 提供 fragment buffer overflow、cross-track DTS regression、invalid defaults、offset overflow、out-of-mdat range 或 truncated fragment
- **THEN** writer 或 reader MUST 在不靜默跳過資料且不進行無界配置的情況下拋出預期診斷

### Requirement: Reproducible MP4 performance baseline
Solution SHALL提供獨立.NET 10 performance benchmark project，以fixed deterministic fixtures分別量測Reader snapshot、Reader sample delivery、progressive/faststart video ingestion、fragmented GOP flush與faststart finalization。Measured operation外MUST完成fixture、sample與output-buffer setup；`allocated bytes` SHALL專指一次measured operation由harness回報的total managed allocated bytes。每份結果MUST記錄runtime、OS、CPU、mode、codec、fixture bytes、logical payload bytes、operation/sample/NAL/GOP counts、Stream type、allocated bytes、GC counts、throughput及同步Stream read/write call counts。

#### Scenario: Capture a pre-change baseline
- **WHEN** developer在修改production copy paths前以Release configuration執行文件化benchmark command
- **THEN** harness MUST完成warmup與至少三次獨立process runs、產生非零operations的machine-readable結果，並記錄各scenario median作為同環境candidate comparison baseline

#### Scenario: Identify and preserve baseline provenance
- **WHEN** harness保存pre-change baseline或candidate results
- **THEN** result MUST記錄source commit SHA、dirty-worktree state、benchmark command、harness version、完整scenario/parameter identity、result path與SHA-256，且comparison MUST拒絕遺漏或不一致identity並回報兩側result paths

#### Scenario: Separate Reader copy costs
- **WHEN** harness量測Reader
- **THEN** constructor snapshot、無event且不讀`Data`的delivery、有event delivery及caller明確讀取`Data` MUST是分開scenarios，不得將fixture setup或input snapshot allocation誤算成sample delivery copy

#### Scenario: Separate Writer layout costs
- **WHEN** harness量測Writer
- **THEN** raw與Annex-B、single與multiple NAL、progressive ingestion、fragmented長短GOP flush及faststart finalization MUST可分別辨識，且output Stream自身的預先配置或成長成本MUST被排除或單獨回報

### Requirement: Measurable allocation improvement without throughput regression
Candidate implementation MUST在同一環境、相同fixtures與相同pre-sized/counting Stream下降低受影響copy paths的total managed allocated bytes。Required acceptance IDs MUST涵蓋Reader video single/multi-NAL no-event delivery、Reader AAC no-event delivery、Writer progressive raw/Annex-B single/multi-NAL ingestion、faststart Annex-B ingestion、fragmented short/long-GOP flush及faststart finalization。對至少1 MiB logical payload的Reader delivery、progressive/faststart ingestion與fragmented flush scenarios，三次獨立process median MUST較pre-change baseline降低至少35%；所有required IDs的throughput median MUST NOT退化超過10%。Machine-readable comparator MUST在identity mismatch或門檻未達時以非零exit code失敗。

#### Scenario: Verify Reader allocation reduction
- **WHEN** candidate benchmark列舉large video NAL與AAC samples且沒有event subscriber也不讀public `Data`
- **THEN** measured-operation total managed allocated bytes median MUST比baseline至少降低35%，並以internal test-visible construction path證明每個emitted payload在caller copy前至多一份owned payload array

#### Scenario: Verify fragmented Writer allocation reduction
- **WHEN** candidate benchmark以預先建立的large multi-NAL samples寫入固定fragmented GOP
- **THEN** measured-operation total managed allocated bytes median MUST比baseline至少降低35%，且allocation growth MUST NOT包含每個NAL normalization array或每個access unit的完整length-prefixed duplicate

#### Scenario: Verify progressive and faststart ingestion allocation reduction
- **WHEN** candidate benchmark以預先建立的large raw或Annex-B single/multi-NAL samples執行progressive或faststart ingestion
- **THEN** measured-operation total managed allocated bytes median MUST比baseline至少降低35%，且output growth/setup MUST被排除或獨立回報

#### Scenario: Reject a material throughput regression
- **WHEN**任一明列required acceptance ID的三次獨立process throughput median比同環境baseline低超過10%
- **THEN** change MUST NOT被標記完成，直到regression被修正或scope經新的spec decision明確調整

### Requirement: Optimization compatibility verification
Verification suite MUST證明copy reduction沒有改變public API、production dependencies、public defensive-copy、exception、stream ownership或MP4 output。固定H.264/H.265、raw/Annex-B、single/multi-NAL、AAC與三種layout fixtures MUST在最佳化前後byte-identical，並MUST通過public Reader round-trip及既有`ffprobe`/`ffmpeg` interoperability validation。

#### Scenario: Preserve public surface and dependencies
- **WHEN** candidate library與pre-change approved API/package baseline比較
- **THEN** production target MUST仍為dependency-free `netstandard2.0`，且MUST沒有新增、移除或改變public type/member signature

#### Scenario: Preserve deterministic output bytes
- **WHEN** candidate Writer以每個fixed codec/layout fixture產生MP4
- **THEN**每個output的SHA-256與box/payload摘要MUST和pre-change baseline相同，且public Reader MUST回傳相同configuration、payload、PTS、DTS、duration、keyframe與event order

#### Scenario: Preserve malformed and ownership behavior
- **WHEN**現有invalid NAL、fragment buffer overflow、malformed Reader payload、repeated enumeration、event mutation及leave-open tests執行
- **THEN** exception types、拒絕時機、語意相等actionable diagnostics、資料隔離及stream close behavior MUST維持既有結果

#### Scenario: Validate optimized outputs externally
- **WHEN**optimized progressive、faststart與fragmented fixtures完成
- **THEN** `ffprobe` MUST辨識相同container/codec/layout，且`ffmpeg -v error` MUST無error-level diagnostics地完成decode validation

### Requirement: Asynchronous API unit and integration verification
Solution SHALL擴充既有獨立unit及integration test projects，對新增Reader/Writer async public surface提供tests-first、deterministic且非零discovered/executed coverage。Unit tests MUST使用async-only、delayed、partial-read/write、cancelling、throwing及non-seekable instrumented Streams驗證virtual async dispatch、token propagation、operation state與failure boundaries，不得以`Thread.Sleep`、GC timing或未控制的ThreadPool排程作唯一證據。Integration tests MUST使用public async API與實際async file-backed Stream驗證H.264/AAC、H.265/AAC及三種layout，並沿用`ffprobe`/`ffmpeg` interoperability checks。

#### Scenario: Prove true async virtual dispatch
- **WHEN**unit tests以同步`Read`/`Write`會失敗、async override由deterministic gate完成的Stream執行Reader及Writer async flows
- **THEN**operations MUST在gate前保持未完成、token MUST傳至每個async I/O、caller Stream的sync read/write count MUST為零，且tests MUST證明沒有deadlock或sync fallback

#### Scenario: Cover cancellation, overlap, and terminal fault
- **WHEN**unit suite依序注入pre-cancel、mid-snapshot、mid-progressive payload、mid-fragment metadata/payload、mid-faststart relocation及concurrent overlap
- **THEN**每個case MUST直接assert output bytes、Reader position、Writer state/accounting、exception/cancellation、後續可用或Faulted行為及leave-open結果

#### Scenario: Compare synchronous, asynchronous, and mixed output
- **WHEN**verification matrix對fixed H.264/H.265、raw/Annex-B、video/AAC及progressive/faststart/fragmented fixtures執行sync、pure async與sequential mixed paths
- **THEN**approved public API addition以外的contract MUST無drift，所有outputs MUST具有相同SHA-256與box/payload摘要，且public Reader round-trip MUST回傳相同configuration、payload、timing、keyframe與event order

#### Scenario: Run asynchronous interoperability integration matrix
- **WHEN**integration project以actual async file-backed Stream與public async API完成六種codec/layout組合
- **THEN**async Reader MUST讀回預期media，`ffprobe` MUST辨識預期container/codecs，`ffmpeg -v error` MUST完成且無error diagnostics；工具不存在時仍 MUST明確回報未完成外部驗證

### Requirement: Console asynchronous round-trip demonstration
.NET 10 Console project SHALL將既有CLI additive擴充為第四個optional `sync`或`async` I/O mode，省略時 MUST保持sync；既有0–3 argument invocation、progressive/H.264 defaults及output file行為 MUST不變，stdout則 MAY additive新增I/O標示而不承諾byte-for-byte不變。Async branch MUST await Writer async factory、canonical async sample/finalization methods與Reader async factory，snapshot完成後 MUST使用既有同步Reader delivery。Console MUST印出`I/O: sync|async`以及既有mode、codec、parsed parameters與所有timed samples。

#### Scenario: Run every asynchronous Console codec and layout
- **WHEN**developer以第四參數`async`執行H.264與H.265的progressive、faststart及fragmented demo
- **THEN**每次執行 MUST印出`I/O: async`、建立可由public Reader與external tools驗證的完整MP4，並 MUST保留預期3個video及4個AAC timed output lines

#### Scenario: Preserve the existing Console default
- **WHEN**developer使用既有output-only或0–3 argument invocation
- **THEN**Console MUST選擇`I/O: sync`、維持既有mode/codec defaults與成功round-trip，不要求caller修改automation

#### Scenario: Reject an unknown Console I/O mode
- **WHEN**developer在第四參數提供不是`sync`或`async`的值
- **THEN**Console MUST顯示包含新參數的actionable usage、回傳nonzero exit code，且 MUST NOT留下被宣稱成功的output

### Requirement: Reproducible asynchronous performance and scalability evidence
Benchmark project SHALL在不新增production dependency的情況下，擴充既有Release capture/compare/self-test workflow，分開量測immediate-completion memory Stream、以`FileOptions.Asynchronous`建立的actual file-backed Stream及deterministic delayed/gated async-only Stream。Scenario identity MUST包含I/O mode、Stream kind、operation、codec/layout/fixture、concurrency與delay/gate parameters；result MUST包含latency/throughput、total managed allocations、GC、sync/async call/byte counts、maximum outstanding async I/O、completed operations、synchronously completed operations/ratio、runtime/environment、commit/dirty state、受控或redacted command/path及hash，且 MUST NOT記錄raw media payload、remote URI、token/credential、任意Stream識別或process-wide敏感資料。Measured async dispatcher MUST在停止計時、擷取metrics與cleanup前await operation完成；concurrency scenario MUST await `Task.WhenAll`。

#### Scenario: Measure immediate-completion async overhead separately
- **WHEN**harness以pre-sized memory Stream比較paired sync/async Reader snapshot、progressive ingestion、fragment flush與faststart finalization
- **THEN**results MUST分別回報ns/op、operations/s、allocation及call counts，且 MUST NOT將memory-only結果描述為普遍I/O scalability改善

#### Scenario: Measure real file-backed asynchronous I/O
- **WHEN**harness以async-enabled temporary file與至少1 MiB deterministic payload執行Reader snapshot及三種Writer layout
- **THEN**fixture/file setup與cleanup MUST排除於measured operation，result MUST識別file-backed Stream並保存可與相同環境sync result比較的metrics

#### Scenario: Prove bounded concurrent asynchronous progress
- **WHEN**harness以concurrency 1、32及128分別對至少一個Reader snapshot與一個代表性Writer layout啟動async-only gated/delayed Stream scenario
- **THEN**dispatcher MUST await所有operations、它們 MUST在gate釋放後完成、maximum outstanding count MUST符合scenario、caller Stream sync I/O count MUST為零且不得deadlock；ThreadPool aggregate observations MAY記錄但 MUST NOT作環境敏感hard gate

#### Scenario: Protect existing synchronous performance
- **WHEN**candidate與pre-change baseline在相同Release harness、相同環境及至少三個獨立process比較既有required sync scenario IDs
- **THEN**每個required sync throughput median MUST NOT退化超過10%，且async數值 MUST以evidence呈現而不得宣稱所有Stream都會提升單次throughput

#### Scenario: Reject incomplete or mismatched async benchmark evidence
- **WHEN**comparator/self-test遇到缺少async identity、不同Stream kind/concurrency/delay、nonzero sync fallback、未完成operation或result provenance/hash不完整
- **THEN**command MUST指出mismatch與result paths並以nonzero exit code失敗

### Requirement: Asynchronous API documentation and compatibility
`README.md` SHALL文件化完整sync與async Reader/Writer範例、Reader async僅涵蓋snapshot、Writer三種layout async範圍、sequential mixing與overlap、cancellation/Faulted/partial-output、leave-open/capability、必要同步control operations、無implicit flush、底層Stream async品質限制、Console第四參數及benchmark commands/metrics。標記的C# usage snippets MUST由automated test抽出並對current library成功編譯。Approved compatibility baseline MUST只接受明列的additive async signatures與正體中文 XML documentation，既有public member、dependency-free `netstandard2.0` target及production package set MUST保持不變；fixed-output hashes MUST不變。

#### Scenario: Follow documented asynchronous usage
- **WHEN**developer依README範例以async factory寫入、await sample/finalization、以async Reader factory重新開啟並使用同步delivery
- **THEN**範例 MUST可編譯並完成有效round-trip，且文件 MUST清楚說明同一Writer overlap、取消後Faulted及temporary-file/rename建議

#### Scenario: Preserve target and package compatibility
- **WHEN**candidate與approved API/package baseline比較
- **THEN**只有核准的async members MAY新增，所有既有signatures MUST不變，production MUST仍為dependency-free `netstandard2.0`，且fixed sync/async output hashes MUST一致

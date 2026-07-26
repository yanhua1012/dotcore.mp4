## ADDED Requirements

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

## ADDED Requirements

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

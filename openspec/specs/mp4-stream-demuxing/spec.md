# MP4 Stream Demuxing Specification

## Purpose

Define supported MP4 stream discovery, timed media delivery, diagnostics, and stream-ownership behavior.

## Requirements

### Requirement: MP4 stream metadata discovery
Reader SHALL 接受 readable、seekable MP4 `Stream`，辨識 supported progressive、faststart 或 fragmented layout，解析最多一個 H.264/H.265 video track 與最多一個 AAC audio track，並在 media delivery 前公開每軌 codec configuration。Reader MUST 從 `avcC` 還原 H.264 SPS/PPS、從 `hvcC` 還原 H.265 VPS/SPS/PPS，並從 `esds` 還原 AAC AudioSpecificConfig 與其解析參數；fragmented layout MUST 以 initial `moov` 的 track ID、sample entries 與 `mvex/trex` 建立 fragment parsing context。

#### Scenario: Discover codec configuration from every layout
- **WHEN** reader 開啟 writer 產生的 progressive、faststart 或 fragmented H.265/AAC MP4
- **THEN** reader MUST 在讀取 samples 前公開原始 VPS、SPS、PPS、AAC AudioSpecificConfig、sample rate 與 channel configuration

#### Scenario: Reject ambiguous mixed sample sources
- **WHEN** MP4 同時以 non-empty progressive sample tables 與 movie fragments 描述同一 supported track
- **THEN** reader MUST 拒絕 ambiguous input，而不得重複或猜測 sample source

### Requirement: Timed video NAL delivery
Reader SHALL 依 decode order 解析 progressive sample tables 或 fragmented `moof/traf/trun` 中的每個 supported video sample，將 length-prefixed payload 拆回原始 NAL units，並為每個 emitted NAL unit 提供 PTS、DTS、duration 與 keyframe 資訊。Reader MUST 在明確 read operation 中為每個 emitted NAL unit 觸發一次既有 video-NAL event。

#### Scenario: Deliver all NAL units for an access unit
- **WHEN** 任一 supported layout 的 video sample 包含多個 H.264 或 H.265 NAL units
- **THEN** reader MUST 依來源順序公開每個 NAL unit，並附上相同 sample timestamps 與 keyframe state

#### Scenario: Recover fragmented video timing and sync state
- **WHEN** fragmented video 使用 `tfdt`、`trun` version 0 或 1、sample flags 與 composition offsets 描述 samples
- **THEN** reader MUST 還原每個 sample 的 DTS、PTS、duration 與 keyframe state，包含 signed negative composition offset

### Requirement: Timed AAC delivery
Reader SHALL 從 progressive sample tables 或 fragmented track runs 公開每個 AAC MP4 sample 的原始 access-unit bytes、PTS、DTS、duration 與 parsed AAC configuration。Reader MUST 在明確 read operation 中為每個 emitted audio sample 觸發一次既有 AAC-sample event，並在 `Read()` 中以跨軌 decode-time order 合併 video 與 audio events。

#### Scenario: Deliver AAC sample event data
- **WHEN** caller 訂閱 AAC event 並讀取任一 supported layout 的 AAC samples
- **THEN** event handler MUST 對每個 AAC sample 收到一次 callback，內容包含 payload、timestamps、duration、sample rate、channel configuration 與 AudioSpecificConfig

#### Scenario: Merge fragmented tracks by decode time
- **WHEN** fragmented MP4 的 video 與 audio samples 分散於多個 `traf` 與 fragments
- **THEN** `Read()` MUST 依各軌 timescale 比較 DTS，並以既有 tie-breaking 行為觸發完整且不重複的跨軌 events

### Requirement: Reader error and ownership semantics
Reader MUST 對 unsupported codecs、malformed length-prefixed NAL、inconsistent progressive tables、malformed fragment metadata、無效 track reference、缺少 timing/default fields、decode-time regression、sample range 不在 referenced `mdat`、資源上限超出或 truncated trailing fragment 拋出可診斷的 `Mp4FormatException`，且 MUST NOT 靜默跳過媒體。Reader SHALL 僅在 caller 明確要求時關閉 caller-owned stream，並 SHALL 維持建構時 snapshot 而非 tail-follow 語意。

#### Scenario: Reject malformed video sample
- **WHEN** 任一 layout 宣告的 video NAL length 超出其 sample boundary
- **THEN** reader MUST 停止讀取並拋出 format exception，而不得輸出 truncated bytes

#### Scenario: Reject sample data outside mdat
- **WHEN** fragmented `trun` 的 computed data range 落在檔案內但不完整位於對應 `mdat`
- **THEN** reader MUST 拒絕 fragment，而不得把 metadata box bytes 當作 media payload

#### Scenario: Reject truncated final fragment
- **WHEN** snapshot 結尾包含不完整的 `moof`、`traf`、`trun` 或 `mdat`
- **THEN** reader MUST 拋出可診斷的 format exception，而不得只回傳先前 fragments

### Requirement: Fragment track and default resolution
Reader SHALL 以 `tkhd.track_ID` 對應 initial tracks、`tfhd.track_ID` 對應 fragment runs，並依 `trun` fields、`tfhd` defaults、`trex` defaults 的優先順序解析 sample duration、size 與 flags。Reader MUST 支援 `first_sample_flags`、同一 `traf` 的多個 `trun`、`default-base-is-moof` 與明確 `trun.data_offset`，並 MUST 拒絕未知或重複的 supported track mapping。

#### Scenario: Resolve inherited sample defaults
- **WHEN** fragmented MP4 將部分 sample duration、size 或 flags 放在 `tfhd` 或 `trex` 而非每個 `trun` entry
- **THEN** reader MUST 依標準優先順序解析每個 sample，並產生與完整 inline fields 相同的結果

#### Scenario: Resolve multiple runs in one track fragment
- **WHEN** 一個 `traf` 包含多個依序排列的 `trun`
- **THEN** reader MUST 正確延續 decode time 與 data addressing，且不得遺漏或重複 samples

#### Scenario: Reject unresolved fragment fields
- **WHEN** sample duration、size 或必要 flags 無法從 `trun`、`tfhd` 或 `trex` 解析
- **THEN** reader MUST 在配置或讀取 sample payload 前拋出指出缺漏欄位的 `Mp4FormatException`

### Requirement: Fragment parsing resource and consistency limits
Reader MUST 以 checked arithmetic 與明確上限約束 top-level fragments、每個 fragment 的 `traf`/`trun` entries、累積 sample count 與 computed ranges，並 MUST 驗證同軌 fragment decode time 不倒退、`mfhd.sequence_number` 單調增加、sample ranges 不 overflow 或 overlap。Fragment support MUST NOT 放寬既有 256 MiB input snapshot、box、descriptor 或 sample limits。

#### Scenario: Reject excessive fragment expansion
- **WHEN** fragment counts 或 declared run sample counts 將超過文件化上限
- **THEN** reader MUST 在大額配置或迴圈展開前拒絕 input

#### Scenario: Reject fragment timeline regression
- **WHEN** 後續 fragment 的同軌 `tfdt` 早於先前已解析 sample 的 decode end
- **THEN** reader MUST 拋出指出 track 與 decode-time regression 的 `Mp4FormatException`

#### Scenario: Reject non-monotonic fragment sequence
- **WHEN** 後續 `mfhd.sequence_number` 重複或小於先前 sequence number
- **THEN** reader MUST 拒絕 input，而不得重新排序 fragments

### Requirement: Payload-efficient Reader delivery
Reader SHALL 在維持 constructor-time完整 snapshot 的前提下，讓每個 emitted video NAL或AAC access unit在 caller明確要求 public defensive copy前，至多建立一份 payload-sized owned array。Reader MUST NOT在內部 payload slice已取得 ownership後，再經 public sample constructor建立第二份相同 payload；既有 public constructors、`Data` properties、events、可重複列舉與 Reader dispose後的sample lifetime MUST維持不變。

#### Scenario: Deliver video without duplicate internal ownership copy
- **WHEN** caller在沒有 event subscriber且不讀取 public `Data` property的情況下列舉含大型single-NAL與multi-NAL samples的任一supported layout
- **THEN** Reader MUST為每個emitted NAL建立至多一份payload-sized owned array，且回傳的payload、timestamps、duration與keyframe state MUST與既有行為相同

#### Scenario: Deliver AAC without duplicate internal ownership copy
- **WHEN** caller在沒有 event subscriber且不讀取 public `Data` property的情況下列舉含大型AAC access units的任一supported layout
- **THEN** Reader MUST為每個emitted access unit建立至多一份payload-sized owned array，且回傳的payload、timestamps與duration MUST與既有行為相同

#### Scenario: Preserve defensive-copy isolation
- **WHEN** caller修改public sample `Data`、video event args `Data`或AAC event args `Data`所回傳的array
- **THEN** 修改MUST NOT影響Reader snapshot、其他event/sample instance、後續重複列舉結果或codec configuration

#### Scenario: Preserve delivered sample lifetime
- **WHEN** caller保留已emitted sample、dispose Reader並釋放input stream
- **THEN** sample的public `Data` MUST仍回傳完整payload，且單一小sample MUST NOT僅因internal最佳化而持有完整MP4 snapshot

#### Scenario: Preserve every public delivery entry point
- **WHEN** caller分別使用`ReadVideoNalUnits()`、`EnumerateVideoNalUnits()`、`ReadAudioSamples()`、`EnumerateAacSamples()`、event-only `Read()`或`ReadAll()`
- **THEN** aliases MUST使用相同optimized ownership path，且payload、callback count、跨軌order、timestamps與defensive-copy isolation MUST符合pre-change behavior

#### Scenario: Validate transferred sample invariants
- **WHEN** internal ownership-transfer path收到null/empty payload、negative PTS/DTS或non-positive duration
- **THEN**它MUST和對應public constructor以相同exception type、`ParamName`及語意相等diagnostic拒絕，且只有payload copy步驟MUST不同

### Requirement: Asynchronous Reader snapshot construction
`Mp4Reader` SHALL 提供 dependency-free `netstandard2.0` 相容且具正體中文 XML documentation 的 `CreateAsync(Stream, bool, CancellationToken)` factory。Factory MUST 使用 caller Stream 的 cancellable `ReadAsync(byte[], int, int, CancellationToken)` 建立與同步 constructor相同的完整 snapshot，並 MUST 保持 readable/seekable capability、256 MiB input limit、codec discovery、error、event、defensive-copy、repeatable enumeration與stream ownership semantics。Snapshot完成後的 parsing及 `ReadVideoNalUnits()`、`ReadAudioSamples()`、`Read()`與aliases SHALL維持同步 memory-only operations，且 implementation MUST NOT以`Task.Run`或async enumeration包裝它們。

#### Scenario: Create a Reader through true async snapshot I/O
- **WHEN** caller以同步`Read`會失敗、`ReadAsync`會延遲完成的readable/seekable Stream呼叫`CreateAsync`
- **THEN** returned task MUST在async reads完成前保持未完成，factory MUST只透過`ReadAsync`取得input bytes，並 MUST建立與同步constructor具有相同configuration、payload、timing及events的Reader

#### Scenario: Restore a nonzero input position
- **WHEN** caller從nonzero original position對有效MP4呼叫`CreateAsync`
- **THEN** factory MUST從stream起點snapshot完整MP4，並在成功後將Stream恢復至原始position

#### Scenario: Cancel before or during snapshot
- **WHEN** token在第一個Stream access前已取消，或在delayed `ReadAsync` loop期間取消
- **THEN** factory task MUST呈現cancellation、MUST在finally嘗試恢復original position、restore成功時MUST回到原位置、MUST NOT回傳partial Reader或關閉caller Stream，且pre-cancel MUST NOT讀取input

#### Scenario: Preserve asynchronous factory failure semantics
- **WHEN** async input宣告過大length、提前結束、缺少required capability、在read/position restore期間失敗或包含既有parser會拒絕的malformed MP4
- **THEN** factory MUST維持對應同步path的resource guard與exception category、MUST NOT輸出partial samples，且在未成功回傳Reader前MUST NOT因`leaveOpen:false`關閉caller Stream；若read/cancellation與finally restore同時失敗，restore exception MUST依既有同步finally語意優先

#### Scenario: Keep post-construction delivery synchronous and repeatable
- **WHEN** caller成功await `CreateAsync`後重複使用任一既有Reader delivery entry point
- **THEN** delivery MUST不再存取input Stream，並 MUST維持既有event order、repeatable enumeration、defensive-copy及Reader dispose後sample lifetime

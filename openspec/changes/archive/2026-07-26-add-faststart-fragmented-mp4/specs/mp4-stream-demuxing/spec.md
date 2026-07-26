## MODIFIED Requirements

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

## ADDED Requirements

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

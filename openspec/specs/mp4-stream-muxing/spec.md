# MP4 Stream Muxing Specification

## Purpose

Define the MP4 writer contract for configured H.264/H.265 and AAC ingestion, timing, ownership, and standards-compliant finalization.

## Requirements

### Requirement: .NET Standard MP4 writer contract
元件 SHALL 提供 .NET Standard 2.0 相容、接受 caller-owned `Stream` 的 writer，並 SHALL 僅在 caller 明確要求時關閉該 stream。未指定 options 的 writer MUST 保持既有 progressive mode；progressive mode MUST 要求 writable、seekable stream，faststart mode MUST 要求 readable、writable、seekable 且支援調整長度的 stream，fragmented mode MUST 僅要求 writable stream。Writer MUST 在寫出任何 MP4 header 前拒絕 null、mode 不支援的 stream capability 或無效 options。

#### Scenario: Preserve default progressive mode
- **WHEN** caller 使用既有 `Mp4Writer(Stream, bool leaveOpen = true)` constructor
- **THEN** writer MUST 產生既有 progressive layout，且 MUST 保持原有 stream ownership 行為

#### Scenario: Reject unsupported progressive or faststart stream
- **WHEN** caller 以 progressive mode 提供不可 seek 的 output，或以 faststart mode 提供不可 read、write、seek 或不可調整長度的 output
- **THEN** writer MUST 拋出可診斷的 argument 或 invalid-operation exception，且 MUST NOT 寫出部分 MP4 header

#### Scenario: Accept non-seekable fragmented output
- **WHEN** caller 以 fragmented mode 提供 writable 但不可 seek 的 output stream
- **THEN** writer MUST 接受該 stream，並 MUST 只以順序寫入完成的 initial metadata 與 fragments

### Requirement: Video codec configuration and NAL ingestion
The writer SHALL accept either H.264 configuration containing SPS and PPS or H.265 configuration containing VPS, SPS, and PPS before accepting video media samples. It MUST accept individual NAL units with PTS, DTS, duration, and key-frame information, and MUST aggregate contiguous NAL units with equal PTS and DTS into one MP4 video sample.

#### Scenario: Write an H.264 access unit from NAL units
- **WHEN** a caller configures H.264 SPS/PPS and submits contiguous key-frame NAL units with equal PTS and DTS
- **THEN** finalization MUST write them as one length-prefixed MP4 sample and advertise the supplied configuration through an `avcC` sample entry

#### Scenario: Write an H.265 access unit from NAL units
- **WHEN** a caller configures H.265 VPS/SPS/PPS and submits contiguous NAL units with equal PTS and DTS
- **THEN** finalization MUST write them as one length-prefixed MP4 sample and advertise the supplied configuration through an `hvcC` sample entry

#### Scenario: Reject incomplete video configuration
- **WHEN** a caller submits H.264 without SPS/PPS or H.265 without VPS/SPS/PPS
- **THEN** the writer MUST reject the operation before the affected sample is committed

### Requirement: AAC ingestion and configuration
The writer SHALL accept raw AAC access units with PTS, DTS, and duration only after a valid AAC AudioSpecificConfig and its declared sample rate and channel configuration are supplied. Each accepted AAC access unit MUST become one MP4 audio sample and its configuration MUST be represented by an `esds` sample entry.

#### Scenario: Write configured AAC samples
- **WHEN** a caller configures AAC-LC AudioSpecificConfig and supplies two timed AAC access units
- **THEN** finalization MUST write an audio track containing two samples with the supplied timing

### Requirement: Standard MP4 finalization
在 progressive mode 成功完成時，writer SHALL 產生包含 `ftyp`、`mdat` 與檔尾 `moov` 的完整 ISO Base Media File Format MP4；其中 video 和/或 audio track 的 sample tables MUST 描述 sample size、chunk offset、decode duration、sync sample 及 PTS 與 DTS 不同時的 composition offset。Writer 在所有 modes MUST 拒絕同軌 DTS 倒退，且 `FinalizeFile()`、`Complete()` 與 `Finish()` MUST 具有一致且 idempotent 的成功完成語意。

#### Scenario: Finalize default mixed recording
- **WHEN** caller 未指定 options，並寫入已設定的 H.264 或 H.265 video 與 AAC audio
- **THEN** resulting stream MUST 是 `ftyp`、`mdat`、`moov` 順序的完整 progressive MP4，且 sample tables MUST 描述所有已接受 samples

#### Scenario: Reject decode-order regression
- **WHEN** submitted sample DTS 早於同軌先前 sample DTS
- **THEN** writer MUST 在提交該 sample 前拒絕操作，並以錯誤指出無效的 timestamp order

#### Scenario: Finalization remains idempotent
- **WHEN** caller 在任一 mode 成功完成後再次呼叫 `FinalizeFile()`、`Complete()` 或 `Finish()`
- **THEN** writer MUST NOT 附加重複 metadata、fragment 或 media payload

### Requirement: Explicit MP4 writer mode selection
Writer SHALL 提供具正體中文 XML documentation 的 public `Mp4WriteMode` 與 `Mp4WriterOptions`，讓 caller 明確選擇 progressive、faststart 或 fragmented layout。Writer MUST 在建構時 snapshot options，且無效 enum、非正值 fragment buffer limit 或後續修改 caller options MUST NOT 造成未定義輸出。

#### Scenario: Select each supported mode
- **WHEN** caller 以有效 options 建立 writer
- **THEN** writer MUST 僅使用所選 mode 的 layout 與 stream contract

#### Scenario: Reject invalid writer options
- **WHEN** caller 提供未知 mode 或非正值的 maximum fragment buffer bytes
- **THEN** writer MUST 在寫入 output 前拒絕 options，並指出無效欄位

### Requirement: Faststart MP4 finalization
Faststart mode SHALL 在成功完成後產生 `ftyp`、`moov`、`mdat` 順序的非 fragmented MP4。Writer MUST 以 bounded-memory second pass 搬移既有 `mdat`，重算所有 chunk offsets，並在位移造成 32-bit offset boundary 改變時正確選擇 `stco` 或 `co64`；它 MUST NOT 將完整 media payload 載入 managed memory。

#### Scenario: Move movie metadata before media
- **WHEN** caller 以 faststart mode 完成一個有效 recording
- **THEN** final output MUST 將完整 `moov` 放在 `mdat` 前，且每個 chunk offset MUST 指向原始 sample payload

#### Scenario: Promote shifted offsets to co64
- **WHEN** faststart 的 moov 位移使任一 adjusted chunk offset 超過 `stco` 可表示範圍
- **THEN** writer MUST 建立穩定長度且使用 `co64` 的 metadata，不得截斷或保留過期 offset

### Requirement: Keyframe-aligned fragmented MP4 output
Fragmented mode SHALL 寫入一個包含 supported track sample entries、空 progressive sample tables 與 `mvex/trex` defaults 的 initial `moov`，再持續寫入一個或多個 `moof` + `mdat` fragments。每個 fragment MUST 使用 movie-fragment-relative addressing、單調遞增的 `mfhd.sequence_number`、每軌 `tfhd`/`tfdt`/`trun`，並 MUST 表示每個 sample 的 duration、size、flags 與必要 composition offset。

#### Scenario: Start fragments at video keyframes
- **WHEN** caller 提交以 keyframe 開始、包含 non-keyframes 並在稍後提交另一個 keyframe 的 video access units
- **THEN** writer MUST 在後一個 keyframe 前完成舊 fragment，且新 keyframe MUST 是下一個 fragment 的第一個 video sample

#### Scenario: Preserve fragmented sample timing
- **WHEN** fragmented input 包含不同 PTS/DTS、duration 與 keyframe state 的 H.264 或 H.265 video 及 AAC audio
- **THEN** `tfdt` 與 `trun` metadata MUST 完整表示 supplied timing、signed composition offset 與 sync state

#### Scenario: Flush final fragment
- **WHEN** caller 在最後一個 GOP 後完成 fragmented writer
- **THEN** writer MUST flush pending video access unit 與最後一個完整 `moof`/`mdat`，且 MUST NOT 產生空 fragment

### Requirement: Fragmented track freezing, ordering, and buffering
Fragmented mode MUST 在第一個 media sample 前至少具備 video configuration，且 MUST 將當時已設定的 supported tracks 寫入 initial `moov` 後凍結。第一個 video access unit MUST 是 keyframe；跨 video/audio tracks 的 submitted DTS MUST 全域非遞減；單一未完成 fragment 的 buffered bytes MUST 受 options 中正值上限約束。Writer MUST 在提交不一致 fragment 前拒絕 audio-only、late configuration、non-keyframe start、cross-track DTS regression 或 buffer overflow。

#### Scenario: Freeze track configuration
- **WHEN** fragmented writer 已接受第一個 media sample，caller 隨後嘗試新增或變更 video/AAC configuration
- **THEN** writer MUST 拒絕變更，且已寫出的 initial `moov` MUST 保持不變

#### Scenario: Reject non-keyframe start
- **WHEN** fragmented recording 的第一個 video access unit 不是 keyframe
- **THEN** writer MUST 在寫出第一個 media fragment 前拒絕該 sample

#### Scenario: Reject cross-track DTS regression
- **WHEN** fragmented caller 在較晚的 global DTS 後提交較早 DTS 的另一軌 sample
- **THEN** writer MUST 拒絕該 sample，並指出 track 與 timestamp ordering violation

#### Scenario: Bound a fragment without a new keyframe
- **WHEN** buffered fragment 在下一個 keyframe 前將超過 configured maximum fragment buffer bytes
- **THEN** writer MUST 在超過上限前明確失敗，且 MUST NOT 自動輸出從 non-keyframe 開始的新 fragment

#### Scenario: Reject audio-only fragmented output
- **WHEN** caller 嘗試在沒有 video configuration 的 fragmented writer 寫入 AAC media
- **THEN** writer MUST 在寫出 initial metadata 或 media bytes 前拒絕操作

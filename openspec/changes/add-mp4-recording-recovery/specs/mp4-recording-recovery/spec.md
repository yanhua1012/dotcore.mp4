## ADDED Requirements

### Requirement: Journal 支援的可復原錄影生命週期
Library SHALL 提供無相依 `netstandard2.0` 相容、具正體中文 XML documentation 的檔案導向可復原錄影 public API，使呼叫端以 target MP4 path 建立、寫入、完成及修復錄影，而不改變既有 `Mp4Writer(Stream, ...)` API。API MUST 在 target 同一資料夾建立唯一 capture MP4 與可重現 sidecar `<target-file-name>.dotcore-journal`；journal MUST 記錄 format version、layout、capture identity、immutable codec configuration、所有已提交 sample 的 offset/size/PTS/DTS/duration/keyframe metadata、commit boundary 與完整性 checksum。journal MUST NOT 包含 raw media payload、remote URI、credential 或 token。

#### Scenario: 建立可復原的 H.264 或 H.265 錄影
- **WHEN** 呼叫端以新的檔案導向 API 建立 progressive、faststart 或 fragmented H.264/AAC 或 H.265/AAC 錄影
- **THEN** library MUST 在 target 同一資料夾建立 capture 與 journal、維持 codec/sample validation 和時間戳契約，且不得改變既有 stream writer 的 API 或預設行為

#### Scenario: 僅提交成功寫入的媒體
- **WHEN** 錄影 write 因 cancellation 或 I/O exception 在媒體 output 中途失敗
- **THEN** journal MUST NOT 宣告未完整 payload 已提交，後續 recovery MUST 至多使用同時具有完整 checksum record 與實際 capture boundary 的 prefix，且不得宣稱未完成錄影已完成

### Requirement: Recovery 重建與 fragment 截斷
Recovery API SHALL 對有效且屬於本元件 journal 的 capture 執行 exact repair。對 progressive，它 MUST 依 journal 已提交 samples 重建 `mdat` length 與檔尾 `moov`；對 faststart，它 MUST 由 immutable capture 在 staging file 產生 `ftyp`/`moov`/`mdat` 並正確調整 chunk offsets；對 fragmented，它 MUST 驗證 initial movie 並只保留完整、相鄰、語意有效的 `moof`/`mdat` pairs，截除所有 trailing partial 或 invalid fragment bytes。無 usable journal 的回退遵循「缺少可用 journal 時的盡力 recovery」requirement。Recovery MUST 以既有 strict `Mp4Reader` 驗證產物，且不得放寬一般 Reader 對 truncated MP4 的拒絕。

#### Scenario: 修復中斷的 progressive 錄影
- **WHEN** progressive capture 已包含已提交 H.264/H.265 video 與 AAC payload，但在 `moov` 寫入前終止
- **THEN** recovery MUST 只使用 journal-confirmed samples 產生完整 `ftyp`/`mdat`/`moov` MP4，並讓 strict Reader 還原 codec configuration、payload、PTS、DTS、duration 與 keyframe state

#### Scenario: 不變更 capture 地修復中斷 faststart 錄影
- **WHEN** faststart 錄影在 finalization 或 delivery 前終止
- **THEN** recovery MUST 由未原地 relocation 的 capture 建立 `ftyp`/`moov`/`mdat` staging output、驗證所有 adjusted chunk offsets，且不得將 capture 視為可安全覆寫的 output

#### Scenario: 保留已完成的 fragmented prefixes
- **WHEN** fragmented capture 在 initial movie 後包含一個或多個完整 fragments 並以 partial `moof`、partial `mdat` 或中繼資料/資料不一致結尾
- **THEN** recovery MUST 保留所有先前完整有效 fragments、丟棄不完整或無效尾端、不得輸出 partial sample，且已修復 output MUST 由 strict Reader 及 standard MP4 consumer 正常解析

### Requirement: 缺少可用 journal 時的盡力 recovery
Recovery API SHALL 在 journal 不存在、為空、checksum/version/identity 不足或 capture boundary 不可用時，依可觀察的輸入證據回報 `Exact`、`Structural`、`Heuristic` 或 `NoRecoverableMedia` recovery tier 及診斷 warnings。若來源是含完整 initial movie 和完整、相鄰、語意有效 `moof`/`mdat` pairs 的 fragmented MP4，implementation MUST 不依賴 journal 進行 `Structural` recovery，僅保留經驗證的完整 pairs。對 progressive 或 faststart，implementation MUST 先嘗試安全結構式路徑；僅當呼叫端明確啟用 heuristic recovery、單一 NAL length width 可一致解析全部 candidate video bytes、且可取得完整 H.264 SPS/PPS 或 H.265 VPS/SPS/PPS 時，才可產生 `Heuristic` video-only output。Heuristic output MUST 建立合成時間、不得復原或猜測 AAC、access-unit grouping 或原始 keyframe/時間戳語意，並 MUST 可由 strict `Mp4Reader` 正常解析。

#### Scenario: 在無 journal 時修復完整 fragmented prefixes
- **WHEN** fragmented 來源沒有 journal 或 journal 為空/不可驗證，但 initial movie 和一個以上完整 valid `moof`/`mdat` pairs 仍存在
- **THEN** recovery MUST 產生只包含精確完整已驗證 fragments 的 `Structural` output、丟棄任何 trailing incomplete bytes，並回報 journal-confirmed 中繼資料不可用

#### Scenario: 明確 opt-in 受限 video-only heuristic 搶救
- **WHEN** progressive 或 faststart 來源沒有 usable journal，但呼叫端明確啟用 heuristic option，且 `mdat` 含有一個無歧義完整 H.264 或 H.265 length-prefixed NAL sequence 與所有必要 parameter sets
- **THEN** recovery MUST 產生帶 warnings 的 `Heuristic` video-only MP4、略過 AAC 而非猜測其 boundaries、丟棄 trailing incomplete NAL，並標示 timing/access-unit/keyframe 語意為合成結果

#### Scenario: 保留空白或證據不足的來源
- **WHEN** 來源為空、缺少完整媒體單元、具有歧義 NAL length widths、缺少必要 parameter sets、含疑似重疊 faststart relocation 資料，或未通過 strict staging validation
- **THEN** recovery MUST 回傳 `NoRecoverableMedia`、MUST NOT 建立或替換 target MP4，並 MUST 保留來源及任何 journal 供診斷

### Requirement: 安全交付、清理與重試語意
錄影 finalization 與 recovery SHALL 在 target 同一資料夾建立 staging output，先關閉並驗證後才交付 target。若 target 已存在，implementation MUST 只使用平台可保證的 same-directory replace operation；無法安全 replace 時 MUST 在不刪除 target、capture 或 journal 下 failure。完成或修復成功後，library MUST 將 journal 標記 completed 並盡力刪除 journal/capture/staging；cleanup failure MUST NOT 將已驗證且已交付的 target 視為失敗。下一次 recovery 遇到完整 target 與 completed/stale journal MUST 只驗證 target 並清理 stale artifacts，不得重建或覆寫媒體。

#### Scenario: 在 successful finalization 或 recovery 後刪除 journal
- **WHEN** finalized 或 recovered staging file 已通過 strict Reader validation 並完成 target delivery
- **THEN** library MUST 嘗試刪除 journal，且刪除成功時不得留下 capture/staging artifact

#### Scenario: 在 cleanup 中斷後保留 stale journal
- **WHEN** target delivery 已成功但 journal deletion 因 process termination、permission 或 transient I/O failure 未完成
- **THEN** 下次 recovery MUST 驗證已完成 target、只嘗試 stale cleanup、不得重複修復或改變 target bytes

#### Scenario: 拒絕 active、corrupted 或 foreign recovery 輸入
- **WHEN** recovery 遇到仍由 active writer 持有的 journal、checksum/version/identity 不符的 journal，或 capture 短於最後已提交 boundary
- **THEN** 它 MUST 以可採取行動的診斷 failure、保留所有 artifacts，且 MUST NOT 刪除或覆寫任何 target file

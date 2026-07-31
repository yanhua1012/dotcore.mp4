## ADDED Requirements

### Requirement: 可重現的錄影 recovery 驗證
Verification suite SHALL 在 recovery implementation 前，為 H.264/AAC 與 H.265/AAC 跨 progressive、faststart、fragmented layouts 新增可重現 tests，且僅使用本機固定 fixtures。Unit tests MUST 在 journal creation、initial 中繼資料、媒體 payload、journal append、progressive 中繼資料、faststart staging/delivery 及每個 fragmented `moof`/`mdat` 邊界注入中斷；它們 MUST 在不使用外部媒體工具、RTSP endpoints、credentials 或依賴時間的 sleeps 下，assert 產生 artifacts、可復原 prefix、target 保留、exception 行為及重試語意。

#### Scenario: 修復每個 codec 與 layout 矩陣組合
- **WHEN** 可重現的中斷為每個 H.264/H.265 × progressive/faststart/fragmented fixture 留下有效 journal/capture prefix
- **THEN** recovery MUST 產生保留預期 codec configuration、已提交 payload、PTS、DTS、duration、keyframe state 與 event order 的輸出，同時只排除未提交或不完整媒體

#### Scenario: 驗證 fragment 尾端截斷位置
- **WHEN** tests 在 multi-keyframe fixture 的每個 `moof`/`mdat` header、中繼資料及 payload 邊界截斷 fragmented output
- **THEN** recovery MUST 精確保留先前完整 fragments、拒絕語意損壞而非猜測，且一般 Reader MUST 持續拒絕未修復來源

#### Scenario: 驗證 journal 清理與冪等重試
- **WHEN** finalization 或 recovery 完成、target delivery 後 cleanup 失敗，或再次呼叫 recovery
- **THEN** tests MUST 證明可行時成功 journal cleanup、delivery 後僅清理 stale artifacts、不重複 reconstruction、不在 unsafe replacement 時覆寫 target，以及任何 failure 後保留輸入

### Requirement: 已修復 MP4 的互通性驗證
Integration tests SHALL 使用公開 recovery APIs 與實際 file-backed streams，修復每個 layout 的 H.264/AAC 和 H.265/AAC fixtures。當 PATH 可取得 `ffprobe` 與 `ffmpeg` 時，它們 MUST 以完整 writer output 相同方式驗證已修復輸出；工具缺失 MUST 保留既有明確 prerequisite reporting。Tests、logs、benchmark artifacts 與文件 MUST NOT 包含 remote RTSP URI、credential、token 或 raw remote media input。

#### Scenario: 以外部工具解碼每個已修復輸出
- **WHEN** recovery 為 progressive、faststart 或 fragmented mode 產生 H.264/AAC 或 H.265/AAC 輸出，且可使用外部工具
- **THEN** `ffprobe` MUST 識別預期 video codec 與 AAC stream，且 `ffmpeg -v error` MUST 在沒有 diagnostics 下完成

#### Scenario: 將遠端串流排除於自動驗證外
- **WHEN** automated unit、integration、benchmark 或文件驗證執行
- **THEN** 它 MUST 使用可重現的本機 fixtures，且 MUST NOT 連線、保存、印出或要求 remote stream URI 或 authentication material

### Requirement: 缺失 journal 的回退驗證
Verification suite SHALL 使用可重現的本機 fixtures，區分精確 journal recovery、無 journal 的 fragmented 結構式 recovery、opt-in progressive/faststart heuristic video 搶救與 `NoRecoverableMedia`。Tests MUST 包含空檔、journal 缺失、零長度 journal、checksum 無效 journal、不足 payload、歧義 NAL length width、缺 H.264/H.265 parameter sets、截斷 terminal NAL、含 AAC 來源及疑似 faststart relocation overlap。Tests MUST assert recovery tier、warnings、target 不建立/不替換、來源保留，以及每個輸出皆通過 strict Reader 驗證。

#### Scenario: 證明回退不會將猜測表示為精確修復
- **WHEN** 無 journal fixture 僅能由結構式 fragment 掃描或明確 heuristic video 搶救修復
- **THEN** unit 與 integration tests MUST 分別 assert `Structural` 或 `Heuristic` tier、可觀察的 warning 內容、heuristic output 沒有 recovered AAC，且不執行會刪除診斷證據的 journal cleanup

#### Scenario: 證明不足輸入不會產生看似成功的輸出
- **WHEN** 回退收到空白或不足媒體證據
- **THEN** tests MUST assert `NoRecoverableMedia`、target 不存在或與既有受保護值 byte-identical，且保留來源/journal

## Context

此為全新專案，目標是提供不依賴原生媒體函式庫的 .NET Standard 2.0 MP4 元件。呼叫端已持有編碼後的 H.264 或 H.265 NAL units 與 AAC access units；元件只負責 ISO Base Media File Format（MP4）封裝與反解析，不編碼、不解碼也不轉碼。使用者提到 VPS，因此本設計將第二種視訊 codec 明確定義為 H.265/HEVC（H.264 不使用 VPS）。

封裝後的檔案必須能被標準工具讀取；整合測試會以本機 `ffprobe` 檢查容器與 stream metadata，再以 `ffmpeg` 解封裝/解碼驗證。產品函式庫不會呼叫或部署這些外部工具。

## Goals / Non-Goals

**Goals:**

- 產生符合 ISO BMFF 的單檔、非 fragmented MP4，並支援單一 H.264 或 H.265 視訊軌與單一 AAC 音訊軌的任意組合。
- 為 H.264 保存 SPS/PPS，為 H.265 保存 VPS/SPS/PPS，並使用 `avcC`/`hvcC`；為 AAC 保存 AudioSpecificConfig 並使用 `esds`。
- 將同一影格時間戳的多個視訊 NAL units 組成一個 MP4 video sample；解析時重新逐 NAL 發出事件。AAC access unit 一個對應一個 audio sample。
- 公開明確、.NET Standard 2.0 相容的 `Stream` API 與事件模型，包含 presentation/decode timestamps、sample duration 與 codec 參數。
- 以測試先行建立 xUnit unit/integration 專案，並提供 .NET 10 Console 的完整寫入、儲存、解析示範。

**Non-Goals:**

- 不負責 RTSP/RTP/WebRTC、影音編解碼、轉碼、音畫同步校正或播放。
- 第一版不支援 fragmented MP4、非 seekable 輸出、加密、字幕、多個同類型軌、B-frame 重排序的自動推導或損壞檔案修復。
- 不從 ADTS header 猜測 AAC 組態；呼叫端必須提供有效的 AAC AudioSpecificConfig 或等價明確參數。

## Decisions

### 以受限的 ISO BMFF 寫入器支援 seekable `Stream`

`Mp4Writer` 接受可寫入且可 seek 的目標 `Stream`，於完成時寫入 `moov` 與 sample table（`stts`、`stsc`、`stsz`、`stco/co64`、`stss`、必要時 `ctts`），產生常見的 progressive MP4。這讓輸出可由多數工具與播放器直接讀取，並避免第一版同時處理 fragmented MP4 的 segment/manifest 語意。

替代方案是對所有 Stream 寫 fragmented MP4，或在記憶體暫存完整內容再一次寫出。前者改變使用者期待的單檔錄影格式且擴大解析範圍；後者使長時間錄影出現無界記憶體使用。因此非 seekable stream 將在開啟時以可診斷的例外拒絕，並在後續版本以 fMP4 擴充。

### 將輸入模型建成已編碼 sample 與明確時間尺度

公共 API 使用 `VideoCodecConfiguration`（codec、NAL length size、SPS/PPS 或 VPS/SPS/PPS）、`AacCodecConfiguration`（AudioSpecificConfig、sample rate、channel configuration）及不可變的 `EncodedVideoNalUnit`/`EncodedAudioSample`。時間以 `TimeSpan` 公開，內部依每軌固定 timescale 轉換為整數 ticks；無法精確轉換的輸入須明確失敗而非靜默捨入。

視訊寫入 API 接收帶 PTS/DTS、duration 與 key-frame 標記的 NAL；相同 PTS/DTS 的連續 NAL 會聚合為一個 access unit。完成或時間戳變更時封存前一個影格。AAC 每個 access unit 都要有 PTS/DTS 及 duration。這既滿足「傳送 NAL unit」的使用方式，也保留 MP4 所需的 sample/access-unit 邊界。

替代方案是把每個 NAL 寫成 sample，或只接受預先組好的 access unit。前者無法可靠產生可解碼影格；後者不符合所要求的 NAL 輸入。因此選擇有明確聚合規則的 NAL API。

### 解析 API 以同步列舉加事件提供完整與即時消費模式

`Mp4Reader` 先解析 boxes、track metadata 與 sample tables，再按 decode order 讀取 sample bytes。視訊 sample 依 `avcC`/`hvcC` 的 length-prefixed 格式拆成 NAL unit；audio sample 直接輸出 AAC bytes。讀取結果會包含 `VideoConfiguration`/`AudioConfiguration` 與帶 PTS、DTS、duration、key-frame 資訊的 sample。`VideoNalUnitRead` 和 `AacSampleRead` 事件逐一公布資料，讓使用者可以訂閱而無須自行分割 sample；同步列舉 API 則可供不想使用事件的呼叫端收集或控制讀取。

事件處理常式會在讀取呼叫的執行緒同步觸發，資料使用新的 `byte[]` 以避免 stream buffer 生命週期洩漏；例外不會被吞掉。這是最適合 .NET Standard 2.0、行為可預測的模型；非同步與零拷貝介面可在不破壞此契約下後續新增。

### 測試先行且以外部工具驗證互通性

先建立 API 契約與 box/sample-table 純邏輯的 xUnit 單元測試，再撰寫以小型已知 H.264/H.265/AAC fixtures 寫入並 round-trip 解析的整合測試。整合測試將定位 PATH 中的 `ffprobe` 與 `ffmpeg`，若缺少則明確 skip 並說明前置條件；在本機 CI 等價環境中必須實際執行。

`ffprobe -show_streams -show_format` 驗證容器、codec、time base 與 stream 數；`ffmpeg -v error -i <file> -map 0 -f null -` 驗證工具能解封裝與解碼。除了工具成功外，測試還會以元件 reader 比對原始 parameter sets、AAC config、NAL/AAC payload、時間戳與 key-frame 資訊。

## Risks / Trade-offs

- [輸出 stream 不可 seek] → 開啟前驗證 `CanWrite`/`CanSeek`，以明確例外及文件指向未來 fMP4 支援，避免產生不完整檔案。
- [未知或不合法的 codec 設定會形成看似有效但無法播放的 MP4] → 在寫入前驗證 NAL 類型、parameter set 完整性、AudioSpecificConfig 與 timestamp 單調性；以 ffmpeg/ffprobe 整合測試覆蓋。
- [原始位元流 fixture 可能受工具版本差異影響] → 使用小型、版控固定的合法 fixtures；斷言結構化欄位與 `ffmpeg -v error`，不比對工具文字輸出的偶然格式。
- [MP4 sample table 與 box offset 實作錯誤] → 將 big-endian 編碼、box 長度、time-scale 轉換及 offset 選擇拆成純函式 unit tests，並加上 round-trip 與外部互通性測試。
- [同 PTS NAL 聚合規則未涵蓋進階碼流] → 第一版要求輸入依 decode order 且同影格 NAL 連續；API 文件與驗證會拒絕倒退的 DTS，後續可新增明確 access-unit builder。

## Migration Plan

這是新元件，無既有資料或 API 遷移。實作時先建立 solution、函式庫與測試專案，再以 tests-first 順序完成；發布前以 `dotnet test` 和已安裝的 ffprobe/ffmpeg 執行整合驗證。

若發現封裝相容性問題，回復該函式庫版本或停止使用新元件即可；它不修改外部檔案以外的狀態。輸出檔案以臨時檔加完成後 rename 的 Console 示範方式降低中途失敗留下成品的風險。

## Open Questions

- 第一版會把 H.265 支援視為需求（因為 VPS 明確被要求）；若「h.264 或 h.264」原意是僅 H.264，實作前應由使用者確認是否降低範圍。
- 第一版以 `TimeSpan` 作為公開時間戳。若整合端已有 90 kHz 或其他原生 clock，未來可在不破壞 API 的前提下新增 tick-based overload。

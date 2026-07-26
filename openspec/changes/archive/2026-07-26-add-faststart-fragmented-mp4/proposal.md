## Why

目前 `Mp4Writer` 只能產生在檔案尾端寫入 `moov` 的 progressive MP4，不適合需要快速起播的完整檔案，也無法在長時間錄製時以 fragments 持續提交可解析的媒體資料。元件需要在不破壞既有預設行為的前提下增加 faststart 與 fragmented MP4，並讓 `Mp4Reader`、Console 範例及驗證流程完整涵蓋三種 layout。

## What Changes

- 保留未指定選項時既有 progressive MP4 的輸出、stream ownership、codec、sample 與時間戳契約。
- 為 `Mp4Writer` 增加明確的輸出選項，可選擇 progressive、faststart 或 fragmented MP4。
- faststart 模式在完成後產生 `ftyp`、`moov`、`mdat` 順序，並修正所有 chunk offsets，讓完整檔案適合 progressive download 快速起播。
- fragmented 模式先寫入包含 track configuration 與 movie-fragment defaults 的 initial `moov`，再於視訊 keyframe 邊界持續輸出 `moof` 與 `mdat`。
- 擴充 `Mp4Reader`，在維持既有列舉與事件 API 的情況下解析 faststart 與 fragmented MP4，還原 H.264/H.265 NAL units、AAC access units、codec configuration、PTS、DTS、duration 與 keyframe 狀態。
- 調整 `DotCore.Mp4.Console`，可選擇並示範三種輸出模式，且以 `Mp4Reader` 重新讀取每種輸出並顯示可觀察的 layout、codec 與 sample 結果。
- 擴充 unit 與 integration tests，涵蓋 box 結構、fragment defaults、offset、keyframe 切割、跨軌順序、錯誤與資源限制，以及本元件和 FFmpeg 產物的雙向互通性。
- 修正測試專案識別，確保標準 `dotnet test` 命令確實執行測試而非以零測試 false green 結束。

## Capabilities

### New Capabilities

無。

### Modified Capabilities

- `mp4-stream-muxing`: 增加向後相容的 writer layout 選項、faststart finalization、keyframe-aligned fragmented output 與其 stream、buffer、順序及錯誤契約。
- `mp4-stream-demuxing`: 增加 faststart 與 fragmented MP4 track/sample discovery、fragment timing、flags、data addressing、資源限制及 malformed input 診斷。
- `mp4-component-verification`: 增加 Console 三模式示範、真實 test discovery、固定 GOP fixtures、writer/reader round-trip、FFmpeg 互通與結構驗證要求。

## Impact

- 主要影響 `src/DotCore.Mp4/Mp4Writer.cs`、`src/DotCore.Mp4/Mp4Reader.cs`、ISO BMFF 內部 box/sample 邏輯及公開 writer options 型別。
- `DotCore.Mp4` 仍維持 `netstandard2.0` 且產品函式庫不新增 native runtime 或 FFmpeg 相依。
- 既有 `Mp4Writer(Stream, bool leaveOpen = true)` 與 `Mp4Reader` 公開讀取 API 保持相容；未指定新選項時輸出仍為既有 progressive layout。
- `samples/DotCore.Mp4.Console`、兩個 test projects、README、OpenSpec 主規格與外部工具整合驗證將隨行為更新。
- 無資料庫、部署、持久化 schema 或外部服務遷移。

## Why

應用程式需要以受控的 .NET 元件，將既有的 H.264/H.265 影像與 AAC 音訊位元流封裝為標準 MP4，並能在不依賴外部播放器的情況下重新解析該檔案。這個元件要提供可串流使用、可驗證的封裝與解析能力，讓錄影、回放及媒體管線可共用一致的時間戳與 codec 組態契約。

## What Changes

- 新增可供 .NET Standard 2.0 使用的 MP4 媒體函式庫，支援寫入 H.264、H.265（含 NAL unit 與 codec parameter sets）及 AAC 至呼叫端提供的 `Stream`。
- 提供 MP4 解析 API，從可讀取的 MP4 `Stream` 還原影像 NAL units、H.264 SPS/PPS、H.265 VPS/SPS/PPS、AAC access units、AAC codec 參數與每個 sample 的時間戳。
- 以事件訂閱方式輸出已解析的影像 NAL unit 與 AAC sample，事件資料包含對應時間戳及必要 codec 參數。
- 建立 xUnit 單元測試與整合測試專案，採測試先行；整合測試使用本機 `ffprobe`/`ffmpeg` 驗證輸出的 MP4 為可辨識且可解碼的標準容器。
- 新增 .NET 10 Console 範例：錄製 MP4 至硬碟，再解析同一檔案並印出每個影像與音訊 sample 及其參數、時間戳。

## Capabilities

### New Capabilities

- `mp4-stream-muxing`: 將帶有時間戳與 codec 組態的 H.264/H.265 與 AAC samples 封裝成標準 MP4 並寫入 `Stream`。
- `mp4-stream-demuxing`: 從 MP4 `Stream` 解析視訊、音訊、codec 組態與逐 sample 時間戳，並透過事件公布媒體資料。
- `mp4-component-verification`: 以 xUnit、ffprobe/ffmpeg 與 .NET 10 Console 範例證明封裝與解析 API 的互通性及可觀測行為。

### Modified Capabilities

- 無（此為新專案，尚無既有規格）。

## Impact

- 新增 .NET Standard 2.0 函式庫、xUnit 單元測試與整合測試專案，以及 .NET 10 Console 範例專案。
- 公開 API 將定義輸入的影像/AAC sample、codec 組態、時間戳與事件模型；具體名稱與例外語意會在設計與規格中固定。
- 整合測試環境必須可找到 `ffprobe` 與 `ffmpeg`；產品函式庫本身不執行或相依這些工具。

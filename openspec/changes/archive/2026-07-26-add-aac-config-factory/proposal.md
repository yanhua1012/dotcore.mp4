## Why

現行在 `AacCodecConfiguration` 當中，使用者建立 AAC 音訊解碼組態時，必須手動組裝或寫死 MPEG-4 `AudioSpecificConfig` 二進位標頭（如 `{ 0x12, 0x10 }`），同時又必須重複傳遞取樣率與聲道數。這種設計容易導致呼叫端出錯或產生重複程式碼。

為解決此問題，提供依據取樣率 (Sample Rate) 與聲道數 (Channel Configuration) 自動編碼 `AudioSpecificConfig` 二進位標頭的工廠方法與建構子重載，並於 `AacCodecConfiguration` 補充詳細的正體中文 XML 文件註解，說明 MPEG-4 AudioSpecificConfig 結構。同步更新 Console 範例專案、Benchmarks、單元/整合測試與 README.md，提升程式碼可讀性與維護性。

## What Changes

- 在 `AacCodecConfiguration` 提供正向自動產生 `AudioSpecificConfig` 的能力（支援 AAC-LC 預設及標準/非標準 Index 15 取樣率編碼，以及 1~7 聲道映射）。
- 新增靜態工廠方法 `AacCodecConfiguration.Create(int sampleRate, int channelConfiguration, int audioObjectType = 2)` 與 `CreateAacLc(int sampleRate, int channelConfiguration)`。
- 新增建構子重載 `AacCodecConfiguration(int sampleRate, int channelConfiguration)`。
- 為 `AacCodecConfiguration` 與相關類別補充詳細的正體中文 XML 文件註解，說明 MPEG-4 AudioSpecificConfig 位元結構、AOT、取樣率索引與聲道配置。
- 重構 `samples/DotCore.Mp4.Console/Program.cs`、`DotCore.Mp4.Benchmarks`、`DotCore.Mp4.Tests`、`DotCore.Mp4.IntegrationTests` 與 `README.md`，簡化 AAC 組態設定。

## Capabilities

### New Capabilities
- `aac-codec-configuration`: 提供依取樣率、聲道數與 Audio Object Type 動態建構與驗證 `AacCodecConfiguration` 及 `AudioSpecificConfig` 二進位標頭的功能與 XML 文件註解。

### Modified Capabilities

## Impact

- `src/DotCore.Mp4/MediaContracts.cs`: 新增 `AacCodecConfiguration` 靜態工廠方法、建構子重載與 XML 文件註解。
- `src/DotCore.Mp4/Internal/AacConfigParser.cs`: 新增 `AudioSpecificConfig` 編碼輔助邏輯或工具方法。
- `samples/DotCore.Mp4.Console/Program.cs`: 使用新工廠方法簡化音訊組態初始化。
- `benchmarks/DotCore.Mp4.Benchmarks/FixedFixtureMatrix.cs` 及 `CompatibilityBaselines.cs`: 更新基準測試矩陣以使用新語法。
- `tests/DotCore.Mp4.Tests/` 及 `tests/DotCore.Mp4.IntegrationTests/`: 新增自動生成 `AudioSpecificConfig` 的單元測試，並重構既有測試。
- `README.md` 及程式碼範例片段: 更新文件範例。

## 1. 測試案例定義與紅燈驗證 (Test-First)

- [x] 1.1 在 `DotCore.Mp4.Tests` 中新增單元測試 `AacCodecConfigurationFactoryTests.cs`，覆蓋標準 44.1 kHz 雙聲道生成 (`CreateAacLc` / 建構子重載)、非標準取樣率 (Index 15) 生成與無效參數 (聲道數 < 1 或 > 7) 驗證
- [x] 1.2 執行測試並確認測試因 `AacCodecConfiguration` 尚無相關工廠方法與建構子重載而編譯失敗或呈現紅燈 (Red Phase)

## 2. 核心 API 實作與綠燈驗證

- [x] 2.1 在 `DotCore.Mp4` 中實作 `AudioSpecificConfig` 編碼 logic（支援 13 種標準取樣率與 Index 15 24-bit 取樣率編碼，以及 1~7 聲道映射）
- [x] 2.2 在 `AacCodecConfiguration` 中實作靜態工廠方法 `Create`、`CreateAacLc` 以及建構子重載 `(int sampleRate, int channelConfiguration)`
- [x] 2.3 為 `AacCodecConfiguration` 補充詳細的正體中文 XML documentation summary 註解
- [x] 2.4 執行單元測試並確認所有測試轉為綠燈 (Green Phase)

## 3. 範例專案、Benchmarks、Tests 與 README 重構

- [x] 3.1 更新 `samples/DotCore.Mp4.Console/Program.cs` 的 AAC 音訊組態建立邏輯，使用 `AacCodecConfiguration.CreateAacLc` 或簡化建構子
- [x] 3.2 更新 `benchmarks/DotCore.Mp4.Benchmarks/FixedFixtureMatrix.cs` 與 `CompatibilityBaselines.cs`
- [x] 3.3 重構既有單元/整合測試 (`ContractTests.cs`、`FixtureData.cs` 等) 採用簡化之 AAC 組態建構方式
- [x] 3.4 更新 `README.md` 中有關 AAC `AacCodecConfiguration` 的範例程式碼
- [x] 3.5 執行全全專案單元測試與迴歸測試，確認編譯與所有測試均全數通過

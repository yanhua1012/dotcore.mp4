## ADDED Requirements

### Requirement: 動態產生 AAC AudioSpecificConfig

系統 SHALL 提供依據 `sampleRate`（取樣率）與 `channelConfiguration`（聲道配置）動態建構 `AacCodecConfiguration` 實例與 `AudioSpecificConfig` 二進位標頭的功能。

#### Scenario: 依標準 44.1 kHz 雙聲道建構 AAC-LC 組態
- **WHEN** 呼叫 `AacCodecConfiguration.CreateAacLc(44100, 2)` 或 `new AacCodecConfiguration(44100, 2)`
- **THEN** 系統回傳 `SampleRate` 為 44100、`ChannelConfiguration` 為 2、`AudioObjectType` 為 2 (AAC-LC) 的 `AacCodecConfiguration` 實例，且 `AudioSpecificConfig` 位元組陣列等於 `{ 0x12, 0x10 }`

#### Scenario: 依非標準取樣率建構 Index 15 AudioSpecificConfig
- **WHEN** 傳入非標準 13 種取樣率（例如 50000 Hz）呼叫 `AacCodecConfiguration.Create(50000, 2)`
- **THEN** 系統產生 5 位元組 (40-bit) 之 `AudioSpecificConfig` 標頭，且取樣率索引編碼為 15 並包含 24-bit 的 50000 頻率數值，經 `AacConfigParser` 解碼後其 `SampleRate` 精確為 50000

#### Scenario: 傳入不合法聲道組態
- **WHEN** 傳入小於 1 或大於 7 的 `channelConfiguration` 呼叫建構子或工廠方法
- **THEN** 系統丟出 `ArgumentOutOfRangeException` 例外

### Requirement: 提供詳細 XML 文件註解與範例更新

系統中的 `AacCodecConfiguration` 類別及公開成員 SHALL 包含詳細的正體中文 XML documentation summary 註解，說明 MPEG-4 AudioSpecificConfig 位元結構。全專案（範例專案、Benchmarks、Tests 與 README.md）SHALL 採用簡化之 AAC 組態建構方式。

#### Scenario: 呼叫端與範例簡化
- **WHEN** 開發者參考 README.md、Console Program.cs 或單元測試
- **THEN** 程式碼範例採用 `AacCodecConfiguration.CreateAacLc(44100, 2)` 或 `new AacCodecConfiguration(44100, 2)` 進行宣告，無須手動硬編碼 `{ 0x12, 0x10 }`

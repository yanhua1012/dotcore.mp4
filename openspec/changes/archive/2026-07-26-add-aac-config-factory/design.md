## Context

目前 `AacCodecConfiguration` 需要呼叫端傳入 `audioSpecificConfig` 位元組陣列以及 `sampleRate` 與 `channelConfiguration`。在撰寫應用程式（例如 `Program.cs`）或測試案例時，呼叫端常需要寫死 `{ 0x12, 0x10 }` 這種 16-bit 標頭。

根據 MPEG-4 Audio (ISO/IEC 14496-3) 規範，`AudioSpecificConfig` 可以在已知 `audioObjectType` (預設為 2 即 AAC-LC)、`sampleRate` (取樣率) 與 `channelConfiguration` (聲道組態 1~7) 的情況下，透過明確的位元運算動態生成。

## Goals / Non-Goals

**Goals:**
- 在 `AacCodecConfiguration` 上增加工廠方法 `Create` / `CreateAacLc` 與建構子重載 `AacCodecConfiguration(int sampleRate, int channelConfiguration)`。
- 自動根據 `sampleRate` 選擇 13 種標準取樣率索引 (Index 0~12，產生 2 位元組標頭) 或非標準取樣率 (Index 15，產生 5 位元組 24-bit 標頭)。
- 為 `AacCodecConfiguration` 補充完整、詳盡的正體中文 XML 文件註解，說明 MPEG-4 AudioSpecificConfig 二進位欄位結構。
- 更新全專案使用到 AAC 組態的地方（Console 範例程式、Benchmarks、Tests、IntegrationTests 以及 README.md）。

**Non-Goals:**
- 支援需要 `program_config_element` (PCE, `channelConfiguration == 0`) 的自訂聲道映射。
- 改變現有 `AacCodecConfiguration` 的不可變性 (Immutability) 與現有 API 相容性（保持現有建構子與 `FromAudioSpecificConfig` 正常運作）。

## Decisions

1. **工廠方法與建構子設計**
   - 增加靜態工廠方法：`AacCodecConfiguration.Create(int sampleRate, int channelConfiguration, int audioObjectType = 2)`
   - 增加便捷靜態方法：`AacCodecConfiguration.CreateAacLc(int sampleRate, int channelConfiguration)`
   - 增加建構子重載：`AacCodecConfiguration(int sampleRate, int channelConfiguration)` （內部呼叫 `CreateAacLc`）
   - **理由**：既能讓語法精簡（如 `new AacCodecConfiguration(44100, 2)`），也能讓意圖明確（如 `AacCodecConfiguration.CreateAacLc(44100, 2)`），且保留支援其他 AOT (如 HE-AAC) 的擴充彈性。

2. **取樣率編碼演算法**
   - 建立內部靜態輔助方法，若 `sampleRate` 為標準 13 種取樣率之一 (96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350)，則編碼為 4-bit Index (0..12)，輸出 2 位元組 (16 bits) `AudioSpecificConfig`。
   - 若 `sampleRate` 為非標準取樣率，則編碼為 Index 15 (`1111`) + 24-bit 頻率整數，輸出 5 位元組 (40 bits) `AudioSpecificConfig`。
   - **理由**：符合 ISO/IEC 14496-3 規範，能與標準播放器及 `AacConfigParser` 解碼器完全相容。

3. **XML 文件註解擴充**
   - 於 `AacCodecConfiguration` 類別標頭及工廠方法補充詳細的正體中文 XML 註解，包含 16-bit 與 40-bit 的位元欄位切割說明（Audio Object Type 5b、Sampling Frequency Index 4b、Channel Configuration 4b、Reserved 3b）。

## Risks / Trade-offs

- **[相容性風險]** 既有 API 保持不變，新增的工廠方法與重載建構子完全為加法變更 (Additive Change)，無重大破壞性風險。
- **[基準測試影響]** 更新 Benchmarks 與 IntegrationTests 以使用新的簡易語法，可提升維護性，且生成的二進位標頭與原本硬編碼的 `{ 0x12, 0x10 }` 位元完全一致。

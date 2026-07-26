## Why

專案中的公開型別與公開成員現狀只有約 23.3% 具備完整的正體中文 XML documentation 註解。許多公開介面（如 `EncodedAudioSample`、`EncodedVideoNalUnit`、`VideoCodecConfiguration` 的建構子與屬性、`VideoNalUnitReadEventArgs`、`AacSampleReadEventArgs`、`Mp4FormatException`、`Mp4Reader` 的同步介面與 `Mp4Writer` 等）欠缺註解，或僅保留英文樣板說明，導致 Visual Studio IntelliSense 開發體驗與 API 文件規格不一致。本變更旨在全面修訂註解規範，並為全專案所有 public API 成員補全規範的正體中文 XML documentation 註解。

## What Changes

- **規範擴充**：於 OpenSpec 規範中明確強制規定公開型別與成員皆須具備正體中文 XML Doc 註解，並適當使用 `<param>`, `<returns>`, `<exception>`, `<remarks>`, `<see cref="...">` 等標準標籤；除了 Codec 名詞、識別碼與標準技術名詞外，說明文句一律為正體中文。
- **Public API 註解補全**：補全 `src/DotCore.Mp4/` 目錄下 5 個公開檔案（`MediaContracts.cs`、`Mp4Exceptions.cs`、`Mp4Reader.cs`、`Mp4Writer.cs`、`Mp4WriterOptions.cs`）共 92 個未完善之公開型別、建構子、屬性、方法與事件的 XML documentation。

## Capabilities

### New Capabilities
- `xml-doc-comments`: 定義全專案公開型別與公開成員的 XML documentation 註解規範與補全要求。

### Modified Capabilities
- （無需求變更）

## Impact

- 影響範圍僅限於 `src/DotCore.Mp4/` 中的 XML 註解文字與 `openspec/config.yaml` 規則規範。
- 不修改任何公開 API 方法簽章、執行階段邏輯或二進位輸出 (Byte-identical)，亦不新增任何套件依賴，保持與現有 `.NET Standard 2.0` 相容。

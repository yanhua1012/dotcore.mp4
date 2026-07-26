# XML Documentation Comments Specification

## Purpose

定義 DotCore.Mp4 全專案所有公開型別與公開成員的正體中文 XML documentation 註解規範與完整涵蓋率要求。

## ADDED Requirements

### Requirement: Mandatory Traditional Chinese XML Documentation for Public Surface
Production library (`DotCore.Mp4`) 中的所有公開型別（class, struct, enum, interface）與公開成員（constructor, property, method, event, enum member）SHALL 包含符合 Visual Studio IntelliSense 標準的 XML documentation 註解。除程式碼識別名稱、API 名稱、標準 Codec 名詞（如 AAC, H.264, HEVC, NAL, AudioSpecificConfig）與關鍵技術名詞外，所有說明文句 MUST 使用正體中文 (zh-TW)。

#### Scenario: Verify presence of XML documentation on public types and members
- **WHEN** 開發者檢視或編譯 `DotCore.Mp4` 中的任何公開 API（包含 `MediaContracts.cs`、`Mp4Exceptions.cs`、`Mp4Reader.cs` 與 `Mp4Writer.cs`）
- **THEN** 所有公開型別與成員皆 MUST 具備 `<summary>` 註解，且說明文字皆為正體中文

#### Scenario: Verify Visual Studio IntelliSense tag compliance
- **WHEN** 公開方法或建構子包含參數、回傳值或例外狀況
- **THEN** 註解 MUST 適當包含 `<param>`, `<returns>`, `<exception>` 等標籤，並於型別或成員參考時使用 `<see cref="...">` 與 `<paramref name="...">`

### Requirement: Complete Coverage Across All Public API Source Files
`DotCore.Mp4` 的公開介面檔案（包含 `MediaContracts.cs`、`Mp4Exceptions.cs`、`Mp4Reader.cs`、`Mp4Writer.cs` 與 `Mp4WriterOptions.cs`）SHALL 達到 100% 的公開成員註解涵蓋率，不得殘留英文樣板註解或遺漏任何公開成員。

#### Scenario: Audit public surface documentation completeness
- **WHEN** 針對 `DotCore.Mp4` 執行公開 API 註解完整性稽核
- **THEN** 共有 120 個公開成員皆符合正體中文與完整 XML 標籤規範，無任何無註解或殘留純英文註解之公開成員

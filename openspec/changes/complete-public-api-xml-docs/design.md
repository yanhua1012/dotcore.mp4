## Context

目前 `DotCore.Mp4` 專案中的公開 API 共有 120 個型別與成員，但其中包含 71 個完全無 XML 註解的成員與 21 個僅有英文或標籤不全的成員。`AacCodecConfiguration` 提供了一套完整的正體中文 XML documentation 範本（包含 `<summary>`, `<param>`, `<returns>`, `<exception>`, `<see cref="...">` 與 `<paramref name="...">` 等）。

為提升程式庫的維護性與開發者體驗，需要訂定具體的 XML Doc 規範並更新 `openspec/config.yaml`，同時對 `src/DotCore.Mp4/` 目錄下的所有公開 API 進行全面補全與修訂。

## Goals / Non-Goals

**Goals:**
- 更新 `openspec/config.yaml` 的 rules 條文，加入詳細的 Visual Studio 正體中文 XML Doc 規範。
- 以 `AacCodecConfiguration` 的註解風格為標準，補全 `MediaContracts.cs`、`Mp4Exceptions.cs`、`Mp4Reader.cs`、`Mp4Writer.cs` 中欠缺或不全的 92 個公開型別與成員註解。
- 確保除了 Codec 名詞、識別碼、標準縮寫與關鍵技術/領域名詞外，說明文句皆使用正體中文。
- 確保變更完全不影響程式碼執行階段邏輯、公開 API 簽章或位元組輸出 (Byte-identical)。

**Non-Goals:**
- 不重構任何公開 API 簽章或內部實作邏輯。
- 不修改 Internal 命名空間下的非公開型別（除非未來公開）。
- 不調整既有 unit/integration 測試程式碼邏輯。

## Decisions

1. **統一註解風格與語言規範**
   - 採用 `AacCodecConfiguration` 註解樣式作為專案標準。
   - 所有 public 類別/結構/Enum/成員皆須具備 `<summary>`。
   - 方法與建構子若包含參數，必須包含 `<param name="...">`；若有回傳值，必須包含 `<returns>`；可能拋出例外時應標註 `<exception cref="...">`。
   - 對於相關型別與參數參考，使用 `<see cref="...">` 與 `<paramref name="...">`。
   - 關鍵技術名詞（如 AAC, H.264, H.265, HEVC, NAL, AudioSpecificConfig, PTS, DTS, GOP, Stream, Hz 等）保持英文，其他文句一律採用正體中文（zh-TW）。

2. **最小變更原則**
   - 僅對檔案中的 XML documentation 註解區塊（`///`）進行修改或補全，絕不變更任何 C# 程式碼邏輯或格式。

## Risks / Trade-offs

- **[Risk] XML Doc 修改可能意外更動邏輯或程式碼格式**
  → **Mitigation**: 修改完成後執行全套 `dotnet build` 與 `dotnet test`（包含 unit tests 與 integration tests），確保 0 warnings、0 errors、測試全部通過。

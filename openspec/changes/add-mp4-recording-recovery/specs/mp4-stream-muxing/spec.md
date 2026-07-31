## ADDED Requirements

### Requirement: 可復原檔案導向 Writer 的相容性
MP4 muxing SHALL 支援由可復原的檔案導向 facade 使用既有 progressive、faststart 與 fragmented 編碼語意，而不改變 `Mp4Writer(Stream, ...)` 的公開 signatures、舊有 progressive/H.264/sync 預設、呼叫端 stream 擁有權、sample 驗證、時間戳語意或既有固定輸出 bytes。可復原 faststart capture MUST NOT 執行既有原地 `mdat` relocation；最終 faststart layout MUST 在獨立 staging output 建立。

#### Scenario: 保留既有 stream Writer 行為
- **WHEN** 呼叫端只使用既有 `Mp4Writer(Stream, ...)` constructors、sync/async write methods 與 finalize aliases
- **THEN** 它 MUST 保留既有 layouts、bytes、stream 擁有權、exception 時機及預設值，且 MUST NOT 建立 journal 或 filesystem artifact

#### Scenario: 將可復原 faststart 交付與 capture 隔離
- **WHEN** 可復原 faststart 錄影接收 samples 後進行 finalization 或 recovery
- **THEN** immutable capture MUST 在獨立 staging file 通過驗證前維持其原始 payload addressing，且只有 staging file MAY 包含 relocated faststart offsets

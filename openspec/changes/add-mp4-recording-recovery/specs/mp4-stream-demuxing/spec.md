## ADDED Requirements

### Requirement: 已修復輸出的 Strict Reader 驗證
`Mp4Reader` SHALL 保持 constructor-time 完整 snapshot 與 strict parsing 語意；它 MUST 持續拒絕 truncated progressive 中繼資料及 truncated trailing fragments。錄影 recovery 產生的修復輸出 MUST 是可由相同公開 Reader constructor 接受的完整 MP4，不得新增 Reader 專用 recovery mode、tail-follow 行為或 silent truncation。

#### Scenario: 透過一般 Reader 解析已修復錄影
- **WHEN** 任一 H.264/H.265 與 progressive/faststart/fragmented fixture 的 recovery 成功
- **THEN** 新的公開 `Mp4Reader` MUST 發現預期 configuration，並精確傳遞 journal-confirmed 或完整 fragment 的 payload、時間、keyframe 與跨軌 event order

#### Scenario: 持續拒絕未修復的截斷輸入
- **WHEN** 呼叫端直接以 `Mp4Reader` 開啟原始中斷 capture 或任意 truncated MP4
- **THEN** Reader MUST 保留既有 format exception 行為，且 MUST NOT 靜默丟棄 trailing bytes 或嘗試 journal discovery

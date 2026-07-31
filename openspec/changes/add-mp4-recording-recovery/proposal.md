## 為什麼

錄影在正常呼叫 `FinalizeFile()` 前若因程序終止、裝置斷電或 I/O 中斷而停止，現有輸出可能缺少可播放所需的中繼資料，或以不完整 fragment 結尾。使用者需要由元件自行保存可驗證的復原資訊，並在後續安全地修復可復原的錄影檔。

## 變更內容

- 新增檔案導向的可復原錄影 API：建立 MP4 時由元件在目標檔同資料夾建立受管理的 sidecar journal。
- 為 progressive 與 faststart 記錄足以重建 movie 中繼資料的 codec configuration、sample、時間戳、keyframe 與已提交輸出邊界；修復時產生完整 MP4。
- 為 fragmented 實作嚴格驗證的復原掃描，保留 initial movie 及所有完整 `moof`/`mdat` pairs，截除未完成的尾端 fragment。
- 在 journal 缺失、空白或不足時提供分級回退：無 journal 的 fragmented 檔仍嘗試安全復原完整 fragments；僅在呼叫端明確 opt-in 時，對具可驗證 H.264/H.265 length-prefixed media 的 progressive/faststart 檔提供標示為 heuristic 的 video-only 嘗試；證據不足時保留來源並回報未修復結果。
- 對 faststart 改用可原子交付的暫存輸出與完成後重新命名，避免原地 `mdat` relocation 在非預期終止後造成不可判定的輸出。
- 定義 journal 生命週期：成功完成或成功修復並驗證後採盡力刪除；清理失敗時保留 stale journal，後續操作只安全清理而不重複修復。
- 補充 H.264/H.265、三種 layout、同步/非同步、截斷位置、修復重試、journal 清理與檔案交付的可重現 unit/integration tests；外部 RTSP 僅作手動、非 CI 的環境參考，不納入原始碼、測試資料或文件。

## 能力

### 新增能力

- `mp4-recording-recovery`: 由 library 管理 sidecar journal、錄影檔中斷復原、檔案交付與 journal 清理的公開契約。

### 修改能力

- `mp4-stream-muxing`: 擴充 writer 的檔案導向輸出與 faststart 安全交付需求，並保持既有以 stream 為主的 API 相容性。
- `mp4-stream-demuxing`: 保留一般 Reader 對不完整 MP4 的嚴格拒絕，並定義修復產物必須能由既有 Reader 正常解析。
- `mp4-component-verification`: 擴充 H.264/H.265、layout、I/O 模式與中斷/修復情境的測試與互通性驗證需求。

## 影響範圍

- 影響 `src/DotCore.Mp4/` 的公開 API、writer 輸出策略、MP4 box 建立與檔案 I/O；production library 維持無相依的 `netstandard2.0`。
- 影響 unit、integration、Console 驗收與 README 文件；不新增 production native runtime、RTSP client 或外部工具依賴。
- 既有 `Mp4Writer(Stream, ...)`、三種 layout 預設、stream 擁有權與 Reader 的 strict parsing 行為維持不變。
- heuristic 回退不保證還原原始 access-unit 邊界、時間戳、keyframe 或 AAC；其結果必須向呼叫端明確標示 recovery tier 與限制。

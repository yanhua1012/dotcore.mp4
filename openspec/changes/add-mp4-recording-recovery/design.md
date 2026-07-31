## 背景

目前 `Mp4Writer` 的以 stream 為主 API 在 progressive 與 faststart mode 於 finalization 才建立完整 `moov`；若程序在此前終止，檔內沒有完整 sample tables。fragmented mode 的 initial `moov` 及每個完整 `moof`/`mdat` pair 可獨立描述已提交媒體，但最後一個 pair 可能被截斷，現有 `Mp4Reader` 會刻意拒絕該檔案。

faststart 現行 finalization 會原地搬移 `mdat` 以將 `moov` 放在前方；該搬移中斷後，來源檔的 payload 可同時含已搬移與未搬移區段，無法可靠辨識。因此復原功能不能直接以既有 `Mp4Writer(Stream, ...)` 或 `Mp4Reader` 的 strict parser 實作。既有檔可能沒有 journal；此時必須在不把猜測包裝成成功的前提下，提供可驗證的結構式修復與呼叫端明確選擇的 heuristic 搶救。

production library 必須維持無相依的 `netstandard2.0`，不使用 RTSP、native media runtime、外部 executable 或任何遠端 URI。提供的實際串流只可由使用者在其受控環境手動驗證，且不得寫入原始碼、規格、測試 fixture、log 或 benchmark artifact。

## 目標與非目標

**目標：**

- 提供以檔案 path 建立的 `Mp4RecordingWriter` 與 `Mp4RecordingRecovery`，由 library 管理工作 MP4、journal、修復暫存檔及安全交付。
- 在 H.264/H.265、AAC、progressive、faststart、fragmented 及 sync/async path 保留所有已確認提交的媒體。
- 以檔案同資料夾的 sidecar journal 保存 progressive/faststart 所需的 codec configuration、sample table 中繼資料、提交邊界與完整性資訊。
- 對 fragmented 檔案保留所有完整且經驗證的 fragments，安全地排除不完整尾端。
- 對缺失或不可用 journal 的來源先嘗試可證明的結構式修復，再以明確 opt-in 提供帶限制與結果等級的 video-only heuristic 搶救。
- 僅在已修復或已完成的 MP4 通過 strict parser 驗證且完成同資料夾交付後，盡力清理 journal。

**非目標：**

- 不從 raw H.264/H.265/AAC payload 猜測 timestamp、AAC sample boundary、keyframe 或 codec configuration。
- heuristic 搶救不保證原始 access-unit grouping、時間戳、keyframe state 或音訊完整性，且不得把其結果標示為 exact recovery。
- 不保證發生媒體/journal 任意位元損壞、儲存體未持久化寫入或跨檔案系統搬移後仍可救回。
- 不把不完整 MP4 放寬給一般 `Mp4Reader`；strict Reader 的拒絕語意維持不變。
- 不新增 RTSP ingest、network credential、native codec、background service 或 production package dependency。

## 設計決策

### 1. 採用獨立的檔案導向 facade，不改變 `Mp4Writer(Stream, ...)`

新增 `Mp4RecordingWriter` 與 `Mp4RecordingRecovery` public surface，封裝既有 writer 的配置、sample write、finalize 與 recovery lifecycle。舊 API 不知道輸出檔路徑，也不應被迫建立 sidecar；其 signatures、output bytes、ownership 及 mode defaults 維持不變。

替代方案是為所有 `Mp4Writer` 自動建立 journal。此方案無法從任意 `Stream` 安全導出可管理 path，且會破壞呼叫端擁有 stream 的契約，故不採用。

### 2. 使用 immutable capture file、sidecar journal 與分階段交付

每個錄影在目標 path 同一資料夾建立：

```text
recording.mp4.dotcore-journal      受管理的 sidecar journal
recording.mp4.dotcore-capture-*    capture MP4；進行中絕不重新命名為最終檔
recording.mp4.dotcore-recover-*    修復/finalization 暫存 MP4
recording.mp4                      僅有已完成且已驗證的交付檔
```

journal header 保存 format version、layout、工作 capture filename 與 target identity；records 保存 immutable codec configuration、已接受 sample 中繼資料、提交輸出邊界與 record checksum。每次 payload 寫入成功後才追加相應 commit record。recovery 僅採用 checksum 有效、完整、且其宣告 capture 邊界未超過實際檔長的最長 prefix；稍後或不完整 journal record 不能擴大可修復範圍。

capture file 是唯一的媒體來源，錄影期間不覆寫其已確認 payload。finalization/recovery 在另一個 staging file 建立完整交付物，驗證成功後才在同一資料夾交付 target。這讓 faststart 永遠不在唯一可復原的 capture file 原地 relocation。

替代方案是直接寫 target，並在修復時 replace。它使 faststart finalization 的原地 relocation 可能破壞唯一 payload source，故不採用。

### 3. 依 layout 採用不同的重建策略

| Layout | Capture 策略 | Recovery 策略 |
| --- | --- | --- |
| Progressive | `ftyp` + open-ended `mdat` + payload；journal 保存所有已提交 sample 中繼資料 | 只複製已提交媒體 prefix，回填 `mdat` length，重建並附加 `moov` |
| FastStart | capture 與 progressive 相同；不在 capture 執行 relocation | 只複製已提交媒體 prefix，以 journal 建立 offset-adjusted `moov`，在 staging 寫 `ftyp`/`moov`/`mdat` |
| Fragmented | 既有 initial `moov` + 連續 `moof`/`mdat` pairs | 驗證 initial movie，掃描並複製完整 pairs 至最後一個完整且語意有效的 fragment；丟棄未完成尾端 |

fragmented recovery 不以 journal 猜測 fragment sample 中繼資料；`moof` 是每個已提交 fragment 的 canonical 中繼資料。journal 仍用來辨識 capture、互斥生命週期及 stale cleanup。

### 4. 缺失 journal 時採用明確的 recovery 層級

recovery result 必須明確回報 `Exact`、`Structural`、`Heuristic` 或 `NoRecoverableMedia` 層級與診斷 warnings：

| 層級 | 前提條件 | 輸出 |
| --- | --- | --- |
| Exact | 完整、可驗證 journal 與 capture prefix | 依 journal 完整重建所有已確認 tracks |
| Structural | 無 journal，但 fragmented initial movie 與完整 fragment pairs 可驗證 | 保留完整 fragments，不猜測中繼資料 |
| Heuristic | 呼叫端明確 opt-in；未完成 progressive/faststart `mdat` 可由單一穩定 NAL length width 全部解析，且可找到完整 H.264 SPS/PPS 或 H.265 VPS/SPS/PPS | 僅輸出 video；每個完整 NAL 作為重建 sample，以呼叫端提供或預設 duration 建立合成 timeline，並包含 warning |
| NoRecoverableMedia | 空檔、無完整媒體、中繼資料/capture 證據不足、journal 損壞或疑似 faststart relocation payload 重疊 | 不建立 target、不刪除來源，回傳診斷 |

Heuristic path 不讀取 remote input、不推測 AAC boundaries、不輸出音訊，並丟棄最後一個不完整 NAL。它在正常 MP4 parser 規則下只使用自洽的 length-prefixed sequence；若多種 width 同時可能、parameter sets 不完整、樣本超出資源限制或 staging output 不能通過 strict Reader validation，必須停止並回傳 `NoRecoverableMedia`。

### 5. 成功標準為 strict validation 後的交付，再做盡力清理

finalization 或 recovery 依序：close/flush staging → 以既有 strict `Mp4Reader` 完整 snapshot/parse → 同資料夾交付 target → 將 journal 標記 completed → 嘗試刪除 journal 與非必要 staging/capture files。journal delete failure 不得將有效 target 改判為 failure；下次 `Recover` 必須先辨識 completed target，再只清理 stale artifacts。

target 已存在時只允許原子 replace path；不能保證 replace 時必須回報 failure 且保留 target/capture/journal，不能靜默覆蓋。所有 temporary files 都在 target directory，禁止跨 volume copy 作為「原子」替代。

### 6. 明確鎖定 operation 擁有權與 failure 語意

active `Mp4RecordingWriter` 必須獨占 journal/capture；recovery 發現 active lock 或 journal ownership 不一致時 fail-fast，不得讀取或清理正在寫入的檔案。任何 external I/O failure/cancellation 發生於 commit/delivery boundary 後，writer/recovery 不得宣稱完成且必須保留 journal/capture。sync/async facade 沿用 `async`/`await`、`ConfigureAwait(false)`、不使用 `Task.Run`、`.Result` 或 `.Wait()`。

## 風險與取捨

- [Journal 與 capture 的 durable ordering 無法由一般 filesystem 保證] → record checksum 與 capture-boundary 檢查只採用兩者共同已觀察到的 prefix；文件明確不承諾任意 power-loss bit integrity。
- [Journal 可能含大量 sample 中繼資料] → 使用 append-only compact binary records、checked limits 與與 Reader 相容的 sample/resource 上限；不記錄 payload 副本。
- [Fragmented 尾端若剛好是完整 box 但中繼資料不一致] → scanner 必須驗證 box boundaries、`moof`/`mdat` pairing、fragment sequencing、track mapping、sample ranges 與時間線，而非僅依 box size 截尾。
- [Atomic replace 跨平台能力不同] → 只在同一 directory 建立 staging；不能取得安全 replace 保證時明確失敗並保留 recovery inputs。
- [Final target 與 stale artifacts 混淆] → journal identity/version、completed marker 與 strict target validation 決定 cleanup；不可因 filename 存在即刪除。
- [Heuristic output 被誤認為精確復原] → 呼叫端必須明確 opt-in，result tier/warnings 可觀察，README 與 tests 明確限制為 video-only salvage。
- [空檔或證據不足產生看似成功的 MP4] → `NoRecoverableMedia` 不建立 target、不改寫來源、保留 artifacts 與診斷。
- [使用者提供的 RTSP credential 被寫入 artifact] → 測試只使用可重現的本機 fixtures；不將 URI、credential 或 remote source 寫入任何程式、文件、測試或輸出。

## 遷移計畫

1. 新增 recovery API、journal format 及內部重建 helpers，不改動現有 `Mp4Writer`/`Mp4Reader` public behavior。
2. 以可重現 fixtures 完成 red/green unit tests，再加入實際檔案 integration 與外部互通性矩陣。
3. 更新 README，說明只有使用檔案導向 facade 才有 recovery 保證、journal path/lifecycle、stale cleanup、failure limits 與安全交付。
4. rollback 時移除新 facade 的採用；既有 stream API 及其輸出不受影響。保留的 journal/capture 可由相同版本 recovery tool 完成或由使用者明確刪除。

## 待釐清問題

- 無；此 change 固定 journal extension、同資料夾 artifact 策略與 target replacement failure 語意。實作時若平台無法提供所需的同目錄安全 replace，必須以明確 exception 保留 artifacts，而非降低交付保證。

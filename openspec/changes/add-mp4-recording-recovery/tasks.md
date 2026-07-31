## 1. Recovery 契約與可重現的 red tests

- [ ] 1.1 新增公開 recovery API 與 XML documentation 的 compile-time/compatibility tests，確認既有 `Mp4Writer(Stream, ...)` 不建立 journal 或額外檔案。
- [ ] 1.2 新增 H.264/H.265 × progressive/faststart 可重現中斷錄影 red tests，assert journal/capture path、已提交 boundary、target 缺席及未實作 recovery API 的預期失敗。
- [ ] 1.3 新增 fragmented `moof`/`mdat` header、中繼資料、payload 與語意損壞 cut-point red tests，assert strict `Mp4Reader` 仍拒絕未修復來源。
- [ ] 1.4 新增 journal checksum/version/identity/capture-length、active-writer exclusivity、unsafe target replacement、cleanup failure 與 stale retry red tests。
- [ ] 1.5 新增 journal 缺失、空白/invalid journal、空檔與不足媒體的 red tests，assert `NoRecoverableMedia`、target 不建立/不替換與來源保留。
- [ ] 1.6 新增無 journal fragmented 結構式 recovery，以及 progressive/faststart 明確 opt-in H.264/H.265 video-only heuristic 搶救 red tests，涵蓋歧義 length width、缺 parameter sets、trailing NAL 與 AAC omission。

## 2. Journal 與 capture 基礎

- [ ] 2.1 實作 internal journal format、checked record serialization/parsing、checksum、resource limits 及 capture identity validation，不保存 raw payload、URI、credential 或 token。
- [ ] 2.2 實作同資料夾 capture/journal/staging path factory、active ownership lock 及 failure cleanup helper，保留呼叫端 target 及失敗 artifacts。
- [ ] 2.3 執行新增 journal/path unit tests，確認從 red 轉 green，並確認任何未完成 record 或 capture 不足都不能成為 recovery input。

## 3. 檔案導向可復原 Writer

- [ ] 3.1 實作具正體中文 XML documentation 的 `Mp4RecordingWriter` sync creation/configuration/sample/finalization facade，將成功 payload write 與 journal commit boundary 正確連結。
- [ ] 3.2 實作 `Mp4RecordingWriter` async factory/write/finalization path，使用 caller-facing async 語意、`ConfigureAwait(false)`，且不使用 `Task.Run`、`.Result`、`.Wait()`或 sync-over-async。
- [ ] 3.3 實作 progressive capture 重建中繼資料 plumbing，使所有 journal-confirmed video/AAC sample tables 可重建並保持 timestamp/keyframe 語意。
- [ ] 3.4 執行 progressive/H.264/H.265 sync/async recovery tests，確認 red cases 轉 green，且既有 Writer bytes/API regression tests 維持通過。

## 4. 安全的 faststart 與 fragmented 重建

- [ ] 4.1 實作 faststart immutable capture 到 staging 的 `ftyp`/offset-adjusted `moov`/`mdat` 重建，禁止在 capture 原地 relocation。
- [ ] 4.2 執行 faststart interruption、offset、staging 及 unsafe-replace red tests，確認只交付已驗證 target 且失敗時保留 journal/capture。
- [ ] 4.3 實作 fragmented initial-movie 與完整 `moof`/`mdat` scanner，驗證 sequence、track mapping、data ranges、timeline 與 box boundaries，僅輸出完整 fragments。
- [ ] 4.4 執行每個 fragment cut-point 與 semantic-corruption red tests，確認只保留前綴且一般 Reader 的 strict rejection 沒有放寬。
- [ ] 4.5 實作 recovery tier/result diagnostics 與無 journal fragmented structural scanner，確認完整 pair 可在沒有 sidecar 時安全交付。
- [ ] 4.6 實作明確 opt-in progressive/faststart video-only heuristic scanner，要求無歧義 length prefix 與完整 H.264/H.265 parameter sets；對 AAC、空檔、內容不足及重疊 payload 回傳 `NoRecoverableMedia`。
- [ ] 4.7 執行 missing-journal、empty/insufficient、heuristic warning 與 target-preservation tests，確認不會將猜測或無媒體結果回報為 exact/success。

## 5. 交付、recovery 生命週期與文件

- [ ] 5.1 實作 `Mp4RecordingRecovery` 的 sync/async recovery、strict `Mp4Reader` validation、same-directory safe delivery 及 completed journal marker。
- [ ] 5.2 實作 finalized/recovered journal 與 capture/staging 的 best-effort cleanup，並讓已交付 target 搭配 stale journal 時只進行 idempotent validation/cleanup。
- [ ] 5.3 執行 lifecycle、cleanup failure、retry、concurrent recovery 與 leave-open/resource disposal tests，確認成功和失敗分支均保留正確 artifacts。
- [ ] 5.4 更新 README 的可復原錄影 usage、journal 命名/lifecycle、recovery 限制、stale cleanup、temporary delivery 及不支援 remote/RTSP ingest 說明，並新增可編譯 snippet tests。

## 6. Integration 與 regression 驗證

- [ ] 6.1 新增實際 file-backed integration matrix：H.264/H.265 × progressive/faststart/fragmented × sync/async interrupted recovery，assert public Reader payload/configuration/timing/keyframe/event order。
- [ ] 6.2 在 `ffprobe`/`ffmpeg` 可用時驗證每個 recovered matrix output，並保持工具缺席時的明確 prerequisite reporting；不得使用或記錄 remote RTSP/credential。
- [ ] 6.3 擴充 file-backed integration matrix，驗證無 journal fragmented structural output 及明確 opt-in H.264/H.265 heuristic video-only output 的 recovery tier、warning、Reader round-trip 與 external decode。
- [ ] 6.4 驗證 empty/insufficient fallback 不建立或覆寫 target，且保留 source/journal；不得將 remote RTSP 或 credential 寫入測試、log、artifact 或文件。
- [ ] 6.5 依序執行 canonical restore、solution build、unit tests、integration tests、README snippet tests 及相關 Console acceptance，確認非零 discovered/passed count、0 failed、0 unexpected skipped、0 warnings/errors。
- [ ] 6.6 執行 benchmark compatibility/fixed-output baseline 與 self-test，確認既有 stream Writer public API、dependency、fixed bytes 及非 recovery layout 沒有漂移。
- [ ] 6.7 執行 `openspec validate --all --strict --json --no-interactive`、requirement/scenario/task 機器計數，以及 `git diff --check` 和 `git diff --cached --check`，將現行命令與結果記錄至 `tasks/todo.md`。

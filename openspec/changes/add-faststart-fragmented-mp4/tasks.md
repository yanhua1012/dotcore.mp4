## 1. 驗證基線與 public contract

- [ ] 1.1 在兩個 xUnit project 明確設定 `IsTestProject=true`，執行標準 solution-level `dotnet test`，記錄修正前零輸出 false green 與修正後 unit/integration 非零測試數。
- [ ] 1.2 為 `Mp4WriteMode`、`Mp4WriterOptions`、options snapshot、預設 progressive mode 與無效 options 撰寫 failing public contract tests。
- [ ] 1.3 為 progressive、faststart、fragmented 各自的 stream capability preflight 與 `leaveOpen` ownership 撰寫 failing tests，包含 fragmented non-seekable output。
- [ ] 1.4 實作具正體中文 XML documentation 的 public mode/options API、向後相容 constructor delegation、options snapshot 與 mode-specific stream validation，使 1.2–1.3 tests 轉綠。
- [ ] 1.5 執行 contract tests 與既有 progressive writer/reader tests，確認未指定 options 的輸出與例外行為沒有 regression。

## 2. Faststart writer

- [ ] 2.1 為 adjusted chunk offsets、`stco/co64` boundary 與 moov 長度收斂邏輯撰寫 failing 純邏輯 tests。
- [ ] 2.2 為 `ftyp`、`moov`、`mdat` top-level order、payload byte preservation、chunk offset 指向與 repeated finalization 撰寫 failing faststart tests。
- [ ] 2.3 為不支援 `SetLength` 的 readable/writable/seekable stream 與 faststart relocation I/O failure 撰寫 failing diagnostic tests，確認 header 前 preflight 與 failure 語意。
- [ ] 2.4 擴充 movie/sample-table builder，使其可使用 checked offset adjustment 並反覆建立 metadata 至 `stco/co64` 與 moov 長度穩定。
- [ ] 2.5 實作固定大小 buffer 的 backward in-place `mdat` relocation 與 faststart finalization，使 2.1–2.3 tests 轉綠且不 snapshot 完整 payload。
- [ ] 2.6 以 `Mp4Reader` round-trip faststart H.264/H.265 與 AAC fixtures，確認 codec、payload、PTS/DTS/duration、keyframe 及 caller stream ownership。

## 3. Fragmented writer box primitives

- [ ] 3.1 為 initial `moov` 的 empty sample tables、track IDs、`mvex/trex` 與 codec sample entries 撰寫 failing box-level tests。
- [ ] 3.2 為 `mfhd`、`tfhd default-base-is-moof`、`tfdt`、`trun.data_offset`、duration、size、flags 與 version 0/1 composition offsets 撰寫 failing fragment-box tests。
- [ ] 3.3 實作可在 bounded seekable memory 中建立 initial movie 與 fragment metadata 的內部 ISO BMFF helpers，使 3.1–3.2 tests 轉綠。
- [ ] 3.4 以獨立 box parser assertions 驗證每個 generated `trun` 的 sample ranges 完整落在後續 `mdat`，且 video/audio payload order 與 offsets 一致。

## 4. Fragmented writer lifecycle

- [ ] 4.1 建立至少含 keyframe、non-keyframe、下一個 keyframe 的固定合法 H.264、H.265 與 AAC test fixtures，記錄 codec 來源與預期 timing/keyframe metadata。
- [ ] 4.2 為第一個 video access unit keyframe、下一 keyframe 開始新 fragment、final fragment flush、無空 fragment與 idempotent finalization 撰寫 failing lifecycle tests。
- [ ] 4.3 為 track freeze、audio-only rejection、late configuration、cross-track DTS regression 與同 DTS 跨軌合法輸入撰寫 failing contract tests。
- [ ] 4.4 為 configurable fragment buffer limit、長 GOP overflow、超限前無 non-keyframe fragment emission 與 non-seekable output 寫入撰寫 failing resource tests。
- [ ] 4.5 實作 fragmented track freeze、global DTS watermark、pending access-unit aggregation、keyframe boundary partition 與 sequence numbering，使 4.2–4.3 tests 轉綠。
- [ ] 4.6 實作 bounded fragment payload buffering、`moof`/`mdat` sequential commit 與 final flush，使 4.4 tests 轉綠。
- [ ] 4.7 執行全部 writer tests並比較 default progressive baseline，確認 fragmented-only ordering restriction 未影響 progressive/faststart。

## 5. Fragmented reader metadata與 defaults

- [ ] 5.1 為 initial `moov` track ID、empty progressive tables、`mvex/trex`、未知/重複 track mapping 與 ambiguous mixed sample sources 撰寫 failing reader tests。
- [ ] 5.2 為 `trun → tfhd → trex` duration/size/flags precedence、`first_sample_flags`、multiple `trun` 與 `default-base-is-moof` addressing 撰寫 failing reader tests。
- [ ] 5.3 重構最小必要的 reader track parsing，分離 codec/sample-description discovery 與 progressive/fragmented sample source，且維持既有 progressive code path。
- [ ] 5.4 實作 `tkhd`/`trex`/`tfhd` track mapping、fragment defaults resolution 與 multiple-run traversal，使 5.1–5.2 tests 轉綠。

## 6. Fragmented reader timing、payload 與 guardrails

- [ ] 6.1 為 `tfdt` decode base、duration accumulation、version 0 unsigned與 version 1 signed composition offsets、sample flags/keyframe 還原撰寫 failing tests。
- [ ] 6.2 為跨 fragments/軌的 decode-time event merge、同 DTS tie-breaking、NAL splitting 與 AAC event data 撰寫 failing tests。
- [ ] 6.3 為 missing `tfdt`、unresolved defaults、non-monotonic `mfhd`、decode-time regression、offset overflow、overlap 與 out-of-`mdat` ranges 撰寫 failing hostile-input tests。
- [ ] 6.4 為 excessive fragments/`traf`/`trun`/sample expansion 與 truncated final `moof`/`mdat` 撰寫 failing resource-limit tests，確認大額配置前拒絕。
- [ ] 6.5 實作 checked fragment timing、flags、composition offsets、data-range resolution 與 `ParsedSample` 轉換，使 6.1–6.2 tests 轉綠並重用既有 delivery API。
- [ ] 6.6 實作 fragment consistency、`mdat` containment、sequence/timeline 與 resource guards，使 6.3–6.4 tests 轉綠。
- [ ] 6.7 執行完整 reader unit suite，確認 progressive、faststart、fragmented 均還原一致的 codec、payload、timing、keyframe 與 ownership 行為。

## 7. Integration、FFmpeg 與 Console

- [ ] 7.1 擴充 integration fixture writer，以 DTS-interleaved 順序為 H.264/AAC 與 H.265/AAC 產生三種 layout，並先加入 failing 三模式 public writer/reader round-trip matrix。
- [ ] 7.2 擴充 PATH-resolved `ffprobe`/`ffmpeg` integration tests，驗證六種 generated codec/layout 組合的 container、streams、fragment state 與無 error decode。
- [ ] 7.3 以 FFmpeg 從固定 source fixtures 產生 `empty_moov + default_base_moof + frag_keyframe` reference files，加入 failing `Mp4Reader` 反向互通 tests 並比對 codec、timing、payload 與 keyframe boundaries。
- [ ] 7.4 修正 writer/reader 互通差異，使 7.1–7.3 integration tests 全數轉綠；不得放寬 malformed-input assertions 或把外部工具失敗改成成功。
- [ ] 7.5 先擴充 Console smoke tests，涵蓋僅 output path 的 progressive 相容行為、三個明確 modes、reader events/output 與未知 mode 非零 exit。
- [ ] 7.6 更新 `DotCore.Mp4.Console` argument parsing、固定 GOP、DTS-interleaved sample submission、mode/options 選擇與 reader round-trip輸出，使 7.5 tests 轉綠。

## 8. 文件、風險檢查與完整驗證

- [ ] 8.1 更新 README 的三模式用途、stream capability、faststart 完成後起播語意、fragmented ordering/buffer/reader snapshot 限制、Console commands 與正確 test discovery commands。
- [ ] 8.2 檢查所有新增 public types/members 的正體中文 XML documentation、`netstandard2.0` 相容性、無新增 native/runtime dependency 與無無關 API 變更。
- [ ] 8.3 執行 security/resource review：checked offsets、box/entry/sample limits、fragment buffer bound、無 payload/log secret 洩漏、malformed input fail-loudly，並補齊發現的 regression tests。
- [ ] 8.4 序列化執行 restore、build、unit tests、integration tests，記錄每個 project 的 discovered/passed/skipped count，且任何零測試或缺工具結果不得宣稱完整通過。
- [ ] 8.5 分別執行 Console progressive、faststart、fragmented outputs，對每個檔案執行 `ffprobe`、`ffmpeg -v error` 與 public `Mp4Reader` observable round-trip。
- [ ] 8.6 執行 `openspec validate add-faststart-fragmented-mp4 --strict --json`、`git diff --check` 與最終 scope review，記錄 rollout 為 opt-in、rollback 為還原新 modes/parser 且既有 progressive 無 migration。

## Context

現有 `Mp4Writer` 在建構時寫入 `ftyp` 與 extended-size `mdat` header，將 media payload 直接寫入 caller-owned seekable stream，並於 `FinalizeFile()` backpatch `mdat` 後把完整 `moov` 與 sample tables 附加在檔尾。writer 只保存 sample 的 offset、size、PTS、DTS、duration 與 keyframe 狀態，不保存已寫出的完整 payload。現有 `Mp4Reader` 則將最多 256 MiB 的完整 seekable input snapshot 到 managed memory，從 `moov/trak/stbl` 建立 sample 清單，再透過既有列舉與同步事件 API 公布 H.264/H.265 NAL units 與 AAC access units。

此次變更必須維持 `netstandard2.0`、managed-only、單一 video track、單一 audio track、caller-owned stream 與既有 public sample/event contracts。未指定新 options 的呼叫端不得改變行為。Console、unit tests、integration tests、README 與 OpenSpec 規格必須一併更新，且驗證命令必須證明有非零測試實際執行。

## Goals / Non-Goals

**Goals:**

- 提供 `Progressive`、`FastStart`、`Fragmented` 三種明確 writer mode，並保留既有 constructor 的 progressive 預設。
- 以 bounded-memory 的 second pass 產生 `ftyp`、`moov`、`mdat` faststart MP4，修正 `stco/co64` offsets。
- 產生單一持續輸出的 fragmented MP4：initial `moov` 後接 keyframe-aligned `moof`/`mdat` pairs。
- 讓 `Mp4Reader` 透明解析三種 layout，並透過既有 API 還原相同 codec、payload、timing 與 keyframe 結果。
- 讓 fragmented output 可寫入僅 `CanWrite` 的 non-seekable stream，且以明確上限約束單一 fragment 暫存。
- 讓 Console 可選模式、寫檔、重新讀取並印出三種模式的可觀察結果。
- 使用本元件 round-trip、固定 GOP fixtures、box-level assertions、FFmpeg 產物反向解析、`ffprobe` 與 `ffmpeg` 解碼建立非循環的互通證據。

**Non-Goals:**

- 不加入編碼、解碼、轉碼、GOP 產生、keyframe 推導或影音同步修正。
- 不加入多 video/audio tracks、字幕、加密、external data references、DASH/HLS manifests、CMAF profile、`sidx` 搜尋 API 或 random-access index。
- 不讓 `Mp4Reader` tail-follow 持續成長中的 stream；reader 仍解析建構時取得的完整 snapshot。
- 不靜默修復或忽略 truncated final fragment、malformed box 或缺漏的 sample metadata。
- 第一版 fragmented mode 不支援 audio-only；沒有 video keyframe 就沒有本次定義的 fragment boundary。
- 不重構與三種 layout、reader 或驗證無直接關係的 codec contracts 與事件模型。

## Decisions

### 以 options object 擴充 writer 並保留既有 constructor

新增具正體中文 XML documentation 的 `Mp4WriteMode` 與 `Mp4WriterOptions`，並新增 `Mp4Writer(Stream, Mp4WriterOptions, bool leaveOpen = true)` overload。`Mp4Writer(Stream, bool leaveOpen = true)` 原封不動地選擇 `Progressive`。writer 在建構時複製並驗證 options，後續修改 caller 的 options instance 不得改變已建立 writer。

`Mp4WriterOptions` 至少包含 `Mode` 與正值的 `MaximumFragmentBufferBytes`；fragment buffer 使用有限且文件化的預設值，讓大型 GOP 的呼叫端能明確調整，同時避免無界 managed-memory 成長。選用 options object 而非多個 boolean constructor，因為 fragment buffer 與後續 layout-specific policy 需要同一個可擴充邊界。

替代方案是只增加 enum overload。它較小，但無法在不持續增加 overload 的情況下提供必要的 fragment buffer guardrail，因此不採用。

### 依 mode 驗證不同 stream capability

`Progressive` 維持 writable + seekable。`FastStart` 使用 in-place second pass，要求 readable + writable + seekable，且在寫入 header 前以不改變長度的 `SetLength` 操作驗證 stream 支援擴展/截斷。`Fragmented` 將每個需要 backpatch 的 box 建在 bounded `MemoryStream`，再順序寫入 caller stream，因此只要求 writable，不要求 readable 或 seekable。

所有模式仍依 `leaveOpen` 決定 ownership。能力不符必須在任何 MP4 bytes 寫出前失敗。

替代方案是在 faststart 模式把完整媒體暫存於記憶體或隱含臨時檔。前者對長時間錄影無界，後者引入檔案系統容量、權限與清理語意，因此不採用。

### FastStart 使用可收斂的 offset 重算與尾端向後搬移

FastStart 的初始寫入沿用 progressive pipeline。Finalization 先以「原 sample offset + candidate moov length」建立 `moov`，並重複計算直到 moov 長度穩定；這涵蓋位移跨越 32-bit boundary 而由 `stco` 切換為 `co64`、進一步改變 moov 長度的情況。接著先擴展 stream，再以固定大小 buffer 從尾端向後搬移完整 `mdat` box，最後把穩定的 `moov` 寫到原 `mdat` 起點。

搬移不得將完整檔案載入記憶體。任一步驟失敗時拋出可診斷例外；與既有 finalization 相同，caller 應在需要原子檔案交付時使用暫存檔完成後 rename。FastStart 不是錄影未完成時的 live playback 保證。

替代方案是在檔頭預留固定 `free`/`moov` 空間。實際 sample count 未知時可能不足或浪費大量空間，且引入額外 sizing option，因此不採用。

### Fragmented writer 凍結 tracks 並在新 keyframe 前 flush 舊 fragment

Fragmented mode 要求至少先設定 video configuration，並在第一個 media sample 到達時凍結當時已設定的 video/audio tracks、寫出 `ftyp` 與 initial `moov`。initial `moov` 包含 sample entries、空的 progressive sample tables、`mvex` 與每軌 `trex`。其後不得新增或修改 codec configuration。

第一個 video access unit 必須是 keyframe。當 writer 看見下一個 video access unit 是 keyframe 時，以該 keyframe DTS 作為邊界：舊 fragment 收納邊界前的 samples，新 keyframe 與 DTS 大於或等於邊界的 audio 屬於新 fragment。`FinalizeFile()` 封存 pending video access unit 並 flush 最後一個 fragment。

每個 fragment 使用單一 `moof`、每軌一個 `traf` 及單一後續 `mdat`。writer 寫入單調遞增的 `mfhd.sequence_number`、`tfhd` 的 `default-base-is-moof`、每軌 `tfdt`，以及帶明確 `data_offset`、duration、size、flags 與必要 composition offset 的 `trun`。PTS-DTS 可能為負時使用可表示 signed composition offset 的 `trun` version 1。

為了在邊界確定前知道舊 fragment 的 audio 已收齊，fragmented mode 額外要求跨軌提交的 DTS 全域非遞減；相同 DTS 可出現在不同軌。違反順序、第一個 video sample 非 keyframe、沒有 video configuration、凍結後變更 configuration 或超過 `MaximumFragmentBufferBytes` 時，writer 必須在提交不一致 fragment 前失敗。

替代方案是允許先送完所有 video 再送 audio，並延後所有 fragments。這會使記憶體隨整段錄影成長且失去持續輸出的目的，因此 fragmented mode 採用更嚴格、可文件化的跨軌順序。

### Fragmented reader 分離 track description 與 sample source

Reader 先解析唯一 initial `moov`，以 `tkhd.track_ID` 建立 track map，從 `stsd` 解析 `avcC`、`hvcC` 與 `esds`。沒有 `moof` 時沿用現有 progressive sample-table pipeline；存在 `moof` 時，要求 supported tracks 的 progressive sample tables 為空，並從 `mvex/trex` 與 top-level fragments 建立 samples，以避免同一媒體被重複計入。

每個 `traf` 依 `tfhd.track_ID` 對應 track，並以 `trun` 欄位優先、`tfhd` defaults 次之、`trex` defaults 最後的順序解析 duration、size 與 flags。DTS 從 `tfdt.baseMediaDecodeTime` 開始依 duration 累加；PTS 為 DTS 加 composition offset；keyframe 由 sample flags 的 non-sync state 還原。Reader 支援 `trun` version 0 unsigned 與 version 1 signed composition offsets、`first_sample_flags`、一個 `traf` 內多個 `trun`，以及 `default-base-is-moof` 的 movie-fragment-relative addressing。

Reader 驗證每個 referenced byte range 完整落在對應 `mdat`，而不只檢查檔案總邊界；同時驗證未知/重複 track ID、缺少 `tfdt`、無法解析的 defaults、decode-time regression、offset overflow、sample overlap、box/traf/trun/sample count limits 與 malformed trailing boxes。成功建立的 fragmented samples 轉成既有 `ParsedSample`，重用 NAL splitting、AAC delivery、事件與跨軌 decode-time merge。

Reader 接受本元件產生的 constrained profile，並涵蓋 FFmpeg 常見的 `empty_moov + default_base_moof + frag_keyframe` layout。它不因支援 fragments 而擴大既有 256 MiB snapshot 與 sample-count 上限。

### Console 與驗證以三模式矩陣提供可觀察證據

Console 保留第一個 positional argument 為 output path，新增第二個可選 mode：`progressive`、`faststart` 或 `fragmented`；省略時仍為 progressive。第三個可選 codec 為 `h264` 或 `h265`，省略時維持 H.264。無效 mode 或 codec 以 usage 與非零 exit code 明確失敗。範例為兩種 codec 使用至少包含 keyframe、non-keyframe、下一個 keyframe 的固定合法 GOP，依 fragmented mode 所需的跨軌 DTS 順序提交 samples，完成後以 `Mp4Reader` 重新讀取並印出選定 mode、codec configuration、NAL/AAC timestamps 與 keyframe 狀態。

Unit tests 驗證 public options、stream capability、box order、offset fixed point、fragment box/default/flags/timing/data ranges、buffer/order guards 與 malformed reader inputs，不呼叫外部工具。Integration tests 對 H.264/AAC 與 H.265/AAC 執行三模式 round-trip，使用 `ffprobe`/`ffmpeg` 驗證所有生成 layout，並以 FFmpeg 產生 reference fMP4 交由本元件 reader 解析。Console smoke tests 執行兩種 codec 的三模式矩陣。

兩個 xUnit project 明確設定 `IsTestProject=true`。標準 solution-level `dotnet test` 必須回報非零 discovered/executed test count；空輸出或零測試不視為成功。

## Risks / Trade-offs

- [FastStart second pass 中斷會留下不完整檔案] → 文件要求需要原子交付時採 temporary path + rename，測試涵蓋搬移與 offset 正確性，不宣稱 in-place operation 可交易式 rollback。
- [FastStart 位移觸發 `stco/co64` 轉換] → 反覆建立 moov 至長度穩定，並以 32-bit boundary 純邏輯測試覆蓋。
- [Fragment 長時間沒有 keyframe 導致記憶體成長] → 可設定且必須為正值的 buffer 上限，超限前明確失敗；不自動切出非 keyframe fragment。
- [跨軌輸入順序比 progressive 嚴格] → 限制只套用 fragmented mode，Console 與 README 提供 DTS-interleaved 範例，例外包含違反的 track 與 timestamp。
- [Fragment defaults 與 data offset 組合容易被惡意輸入利用] → 以 checked arithmetic、box/mdat containment、entry/sample limits 與 hostile-input tests 在配置或讀取前拒絕。
- [Writer/reader 自我一致掩蓋格式錯誤] → 加入 FFmpeg 生成檔的反向 reader 驗證及本元件輸出的 ffprobe/ffmpeg 驗證。
- [Reader snapshot 仍限制大型長時間 fMP4] → 保留既有 256 MiB 明確限制；streaming reader 是不同 API 與生命週期問題，不在此次擴張。
- [Console 介面變更破壞既有用法] → 保留 output path 第一參數與省略 mode 的 progressive 預設。

## Migration Plan

1. 先修正 test project discovery，保存現有 progressive baseline 與非零測試數。
2. 加入 options 與 mode-specific stream validation；既有 constructor 仍走原 progressive code path。
3. 以 tests-first 加入 faststart writer 與 progressive reader round-trip。
4. 以 tests-first 加入 constrained fragmented writer，再加入 fragmented reader 與 hostile-input guards。
5. 擴充固定 GOP fixtures、Console、integration matrix、README 與 ffprobe/ffmpeg commands。
6. 完整執行 restore、build、unit、integration、三模式 Console、ffprobe、ffmpeg 與 OpenSpec strict validation。

此變更沒有資料 migration。若需 rollback，還原新增 options、faststart/fragmented code paths、reader fragment parser及其文件/測試；既有 progressive constructor 與檔案格式沒有遷移需求。發布時新模式為 caller 明確 opt-in，因此可先部署元件，再逐一啟用呼叫端。

## Open Questions

無；此次規劃固定為 writer 與 reader 同步支援三種 layout，fragmented reader 採 snapshot 而非 live tail-follow。

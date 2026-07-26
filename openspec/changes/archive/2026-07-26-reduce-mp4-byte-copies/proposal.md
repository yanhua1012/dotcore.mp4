## Why

目前 `Mp4Reader` 在完整 snapshot 後仍會為每個 media payload 進行重複配置與複製，`Mp4Writer` 也會在 video NAL normalization、fragment buffering 與 metadata 建構中產生可避免的 payload-sized arrays。Repository 尚無可重現的 allocation/throughput baseline，因此需要先建立量測，再以不改變公開 ownership 與 MP4 輸出的方式降低 CPU、GC 與 memory-bandwidth 成本。

## What Changes

- 新增獨立的 .NET 10 performance benchmark harness，以固定 progressive、faststart、fragmented fixtures 分別量測 Reader snapshot、sample delivery、Writer ingestion、fragment flush 與 faststart finalization。
- 調整 Reader 的 internal payload ownership transfer，移除 `Slice` 後由 public sample constructor 再 defensive-copy 的重複複製，但保留既有 public constructors、events 與 `byte[] Data` defensive-copy 行為。
- 將 Writer 的 internal NAL normalization 改為描述已擁有 sample buffer 的 byte ranges，避免為 raw 或 Annex-B video 額外建立 payload-sized NAL arrays。
- 降低 fragmented Writer 對 length-prefixed video payload 與 `moof` provisional/final buffers 的重複 materialization，同時維持 fragment buffer 上限、keyframe boundary 與 sequential-output 契約。
- 新增 allocation regression、public API compatibility、byte-identical fixture、Reader round-trip 及 `ffprobe`/`ffmpeg` interoperability 驗證。
- 不在此 change 新增 async I/O、streaming Reader、public borrowed-memory API、production dependency或 faststart layout redesign；這些保留為獨立後續變更。

## Capabilities

### New Capabilities

- 無。

### Modified Capabilities

- `mp4-stream-demuxing`: Reader media delivery 必須避免內部重複 payload ownership copy，同時維持 constructor-time snapshot、可重複列舉、event 與 public defensive-copy 語意。
- `mp4-stream-muxing`: Writer video ingestion、fragment buffering 與 fragment metadata resolution 必須避免不必要的 payload-sized materialization，同時產生相同 MP4 bytes 與錯誤行為。
- `mp4-component-verification`: 驗證結構新增可重現的 performance baseline、allocation regression gate、public API compatibility 與最佳化前後輸出比較。

## Impact

- 受影響程式碼：`Mp4Reader` payload construction、media contracts 的 internal construction path、`NalUnits` normalization、fragment sample representation、`moof` offset resolution 與 BMFF in-memory buffers。
- 受影響驗證：unit/integration tests、FFmpeg interoperability matrix、README，以及新的 benchmark project。
- Public API、同步呼叫模式、stream ownership、snapshot 上限、三種 layout、timestamp/event ordering，以及 exception types、rejection timing與語意相等actionable diagnostics 維持不變。
- Production library 維持 dependency-free `netstandard2.0`；benchmark project 可使用 .NET 10 與僅限 benchmark 的套件。

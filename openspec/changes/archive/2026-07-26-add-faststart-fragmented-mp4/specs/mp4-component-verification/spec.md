## MODIFIED Requirements

### Requirement: Test-first project structure
Solution SHALL 維持 .NET Standard 2.0 production library、獨立 xUnit unit-test project、獨立 xUnit integration-test project 與 .NET 10 Console demonstration project。兩個 test projects MUST 明確宣告可由標準 `dotnet test` discovery 執行；任何 behavior 的 tests MUST 在其 production implementation task 完成前加入，且完成驗證 MUST 回報非零 discovered/executed test count。

#### Scenario: Run unit tests independently
- **WHEN** developer 執行 unit-test project
- **THEN** tests MUST 在不呼叫 ffmpeg 或 ffprobe 的情況下驗證 box encoding、codec configuration、NAL aggregation/splitting、timestamp、progressive tables、faststart relocation、fragment construction/parsing、resource guards 與 error paths

#### Scenario: Prevent a zero-test false green
- **WHEN** developer 以文件化的 solution-level `dotnet test` command 執行驗證
- **THEN** command MUST 實際 discover 並執行兩個 test projects 的非零測試，且零測試或空白 test output MUST NOT 被記錄為成功

### Requirement: MP4 interoperability integration verification
Integration-test project SHALL 透過 public writer API 為 H.264/AAC 與 H.265/AAC 產生 progressive、faststart、fragmented MP4，透過 public reader API 重新解析，並比較 codec configuration、media payload、PTS、DTS、duration 與 keyframe 資訊。它 MUST 以 PATH-resolved `ffprobe` 與 `ffmpeg` 驗證每種 generated layout，並 MUST 以 FFmpeg 產生的 common fragmented MP4 驗證 reader，而不得僅依賴本元件 writer/reader 自我一致。

#### Scenario: Validate every generated layout with external tools
- **WHEN** integration tests 完成 H.264/AAC 或 H.265/AAC 的任一 writer mode 且 `ffprobe` 與 `ffmpeg` 可從 PATH 取得
- **THEN** `ffprobe` MUST 辨識預期 container、codec streams 與 fragmented state，且 `ffmpeg` MUST 無 error-level diagnostics 地完成 decode validation

#### Scenario: Read an independently generated fragmented MP4
- **WHEN** FFmpeg 產生含固定 GOP、initial moov 與多個 keyframe-aligned fragments 的 H.264/AAC 或 H.265/AAC MP4
- **THEN** public `Mp4Reader` MUST 還原預期 codec、payload、timing 與 keyframe boundaries

#### Scenario: Report missing external verification tools
- **WHEN** ffprobe 或 ffmpeg 無法從 PATH 取得
- **THEN** integration test MUST 回報清楚命名缺少 executable 的 skipped 或 failed prerequisite，且 MUST NOT 宣稱 interoperability verification passed

### Requirement: Console round-trip demonstration
.NET 10 Console project SHALL 保留 output MP4 path 為第一個 positional argument，接受第二個可選的 `progressive`、`faststart` 或 `fragmented` mode，省略時 SHALL 使用 progressive；第三個可選 codec SHALL 接受 `h264` 或 `h265`，省略時 SHALL 使用 H.264。它 MUST 使用含 keyframe、non-keyframe 與下一個 keyframe 的固定合法 H.264/H.265/AAC samples，以所選 mode 與 codec 建立並完成 MP4、重新開啟該檔、訂閱 video-NAL 與 AAC events，並印出 selected mode、parsed codec parameters、每個 emitted sample 的 timestamps 與 video keyframe state。

#### Scenario: Run each demonstration mode
- **WHEN** developer 分別以 H.264 與 H.265 執行 progressive、faststart 與 fragmented Console mode
- **THEN** 每次執行 MUST 在指定 path 寫出該 codec/layout 的完整 MP4，透過 public `Mp4Reader` 讀回，並印出 mode、codec、parsed parameters 與所有預期 video/AAC samples

#### Scenario: Preserve existing Console invocation
- **WHEN** developer 只提供既有的 output path argument
- **THEN** Console MUST 產生 progressive MP4，並維持成功 round-trip 行為

#### Scenario: Reject an unknown Console mode
- **WHEN** developer 提供不是 progressive、faststart 或 fragmented 的 mode
- **THEN** Console MUST 顯示可操作的 usage、回傳非零 exit code，且 MUST NOT 留下被宣稱成功的 output

## ADDED Requirements

### Requirement: Layout-specific structural verification
Verification suite SHALL 使用至少含兩個 keyframes 且中間含 non-keyframe 的固定合法 GOP，直接檢查三種 top-level box order、faststart chunk offsets、fragment count、keyframe boundaries、track defaults、decode times、sample flags、composition offsets 與 `mdat` ranges。Fixtures MUST 固定且可重現，不得依賴隨機 timing 或未記錄的 encoder 狀態。

#### Scenario: Prove faststart placement and offsets
- **WHEN** tests 完成 faststart fixture
- **THEN** assertions MUST 證明 `moov` 位於 `mdat` 前、所有 sample offsets 指向預期 payload，且 reader round-trip 與外部 decode 均通過

#### Scenario: Prove keyframe fragment boundaries
- **WHEN** tests 寫入 keyframe、non-keyframe、下一個 keyframe 的 fragmented fixture
- **THEN** assertions MUST 證明第二個 keyframe 開始新的 fragment、non-keyframes 留在前一 GOP，且 reader 回報相同 keyframe state

#### Scenario: Verify bounded and malformed paths
- **WHEN** unit tests 提供 fragment buffer overflow、cross-track DTS regression、invalid defaults、offset overflow、out-of-mdat range 或 truncated fragment
- **THEN** writer 或 reader MUST 在不靜默跳過資料且不進行無界配置的情況下拋出預期診斷

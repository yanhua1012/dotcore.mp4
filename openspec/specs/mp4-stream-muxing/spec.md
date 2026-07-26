# MP4 Stream Muxing Specification

## Purpose

Define the MP4 writer contract for configured H.264/H.265 and AAC ingestion, timing, ownership, and standards-compliant finalization.

## Requirements

### Requirement: .NET Standard MP4 writer contract
元件 SHALL 提供 .NET Standard 2.0 相容、接受 caller-owned `Stream` 的 writer，並 SHALL 僅在 caller 明確要求時關閉該 stream。未指定 options 的 writer MUST 保持既有 progressive mode；progressive mode MUST 要求 writable、seekable stream，faststart mode MUST 要求 readable、writable、seekable 且支援調整長度的 stream，fragmented mode MUST 僅要求 writable stream。Writer MUST 在寫出任何 MP4 header 前拒絕 null、mode 不支援的 stream capability 或無效 options。

#### Scenario: Preserve default progressive mode
- **WHEN** caller 使用既有 `Mp4Writer(Stream, bool leaveOpen = true)` constructor
- **THEN** writer MUST 產生既有 progressive layout，且 MUST 保持原有 stream ownership 行為

#### Scenario: Reject unsupported progressive or faststart stream
- **WHEN** caller 以 progressive mode 提供不可 seek 的 output，或以 faststart mode 提供不可 read、write、seek 或不可調整長度的 output
- **THEN** writer MUST 拋出可診斷的 argument 或 invalid-operation exception，且 MUST NOT 寫出部分 MP4 header

#### Scenario: Accept non-seekable fragmented output
- **WHEN** caller 以 fragmented mode 提供 writable 但不可 seek 的 output stream
- **THEN** writer MUST 接受該 stream，並 MUST 只以順序寫入完成的 initial metadata 與 fragments

### Requirement: Video codec configuration and NAL ingestion
The writer SHALL accept either H.264 configuration containing SPS and PPS or H.265 configuration containing VPS, SPS, and PPS before accepting video media samples. It MUST accept individual NAL units with PTS, DTS, duration, and key-frame information, and MUST aggregate contiguous NAL units with equal PTS and DTS into one MP4 video sample.

#### Scenario: Write an H.264 access unit from NAL units
- **WHEN** a caller configures H.264 SPS/PPS and submits contiguous key-frame NAL units with equal PTS and DTS
- **THEN** finalization MUST write them as one length-prefixed MP4 sample and advertise the supplied configuration through an `avcC` sample entry

#### Scenario: Write an H.265 access unit from NAL units
- **WHEN** a caller configures H.265 VPS/SPS/PPS and submits contiguous NAL units with equal PTS and DTS
- **THEN** finalization MUST write them as one length-prefixed MP4 sample and advertise the supplied configuration through an `hvcC` sample entry

#### Scenario: Reject incomplete video configuration
- **WHEN** a caller submits H.264 without SPS/PPS or H.265 without VPS/SPS/PPS
- **THEN** the writer MUST reject the operation before the affected sample is committed

### Requirement: AAC ingestion and configuration
The writer SHALL accept raw AAC access units with PTS, DTS, and duration only after a valid AAC AudioSpecificConfig and its declared sample rate and channel configuration are supplied. Each accepted AAC access unit MUST become one MP4 audio sample and its configuration MUST be represented by an `esds` sample entry.

#### Scenario: Write configured AAC samples
- **WHEN** a caller configures AAC-LC AudioSpecificConfig and supplies two timed AAC access units
- **THEN** finalization MUST write an audio track containing two samples with the supplied timing

### Requirement: Standard MP4 finalization
在 progressive mode 成功完成時，writer SHALL 產生包含 `ftyp`、`mdat` 與檔尾 `moov` 的完整 ISO Base Media File Format MP4；其中 video 和/或 audio track 的 sample tables MUST 描述 sample size、chunk offset、decode duration、sync sample 及 PTS 與 DTS 不同時的 composition offset。Writer 在所有 modes MUST 拒絕同軌 DTS 倒退，且 `FinalizeFile()`、`Complete()` 與 `Finish()` MUST 具有一致且 idempotent 的成功完成語意。

#### Scenario: Finalize default mixed recording
- **WHEN** caller 未指定 options，並寫入已設定的 H.264 或 H.265 video 與 AAC audio
- **THEN** resulting stream MUST 是 `ftyp`、`mdat`、`moov` 順序的完整 progressive MP4，且 sample tables MUST 描述所有已接受 samples

#### Scenario: Reject decode-order regression
- **WHEN** submitted sample DTS 早於同軌先前 sample DTS
- **THEN** writer MUST 在提交該 sample 前拒絕操作，並以錯誤指出無效的 timestamp order

#### Scenario: Finalization remains idempotent
- **WHEN** caller 在任一 mode 成功完成後再次呼叫 `FinalizeFile()`、`Complete()` 或 `Finish()`
- **THEN** writer MUST NOT 附加重複 metadata、fragment 或 media payload

### Requirement: Explicit MP4 writer mode selection
Writer SHALL 提供具正體中文 XML documentation 的 public `Mp4WriteMode` 與 `Mp4WriterOptions`，讓 caller 明確選擇 progressive、faststart 或 fragmented layout。Writer MUST 在建構時 snapshot options，且無效 enum、非正值 fragment buffer limit 或後續修改 caller options MUST NOT 造成未定義輸出。

#### Scenario: Select each supported mode
- **WHEN** caller 以有效 options 建立 writer
- **THEN** writer MUST 僅使用所選 mode 的 layout 與 stream contract

#### Scenario: Reject invalid writer options
- **WHEN** caller 提供未知 mode 或非正值的 maximum fragment buffer bytes
- **THEN** writer MUST 在寫入 output 前拒絕 options，並指出無效欄位

### Requirement: Faststart MP4 finalization
Faststart mode SHALL 在成功完成後產生 `ftyp`、`moov`、`mdat` 順序的非 fragmented MP4。Writer MUST 以 bounded-memory second pass 搬移既有 `mdat`，重算所有 chunk offsets，並在位移造成 32-bit offset boundary 改變時正確選擇 `stco` 或 `co64`；它 MUST NOT 將完整 media payload 載入 managed memory。

#### Scenario: Move movie metadata before media
- **WHEN** caller 以 faststart mode 完成一個有效 recording
- **THEN** final output MUST 將完整 `moov` 放在 `mdat` 前，且每個 chunk offset MUST 指向原始 sample payload

#### Scenario: Promote shifted offsets to co64
- **WHEN** faststart 的 moov 位移使任一 adjusted chunk offset 超過 `stco` 可表示範圍
- **THEN** writer MUST 建立穩定長度且使用 `co64` 的 metadata，不得截斷或保留過期 offset

### Requirement: Keyframe-aligned fragmented MP4 output
Fragmented mode SHALL 寫入一個包含 supported track sample entries、空 progressive sample tables 與 `mvex/trex` defaults 的 initial `moov`，再持續寫入一個或多個 `moof` + `mdat` fragments。每個 fragment MUST 使用 movie-fragment-relative addressing、單調遞增的 `mfhd.sequence_number`、每軌 `tfhd`/`tfdt`/`trun`，並 MUST 表示每個 sample 的 duration、size、flags 與必要 composition offset。

#### Scenario: Start fragments at video keyframes
- **WHEN** caller 提交以 keyframe 開始、包含 non-keyframes 並在稍後提交另一個 keyframe 的 video access units
- **THEN** writer MUST 在後一個 keyframe 前完成舊 fragment，且新 keyframe MUST 是下一個 fragment 的第一個 video sample

#### Scenario: Preserve fragmented sample timing
- **WHEN** fragmented input 包含不同 PTS/DTS、duration 與 keyframe state 的 H.264 或 H.265 video 及 AAC audio
- **THEN** `tfdt` 與 `trun` metadata MUST 完整表示 supplied timing、signed composition offset 與 sync state

#### Scenario: Flush final fragment
- **WHEN** caller 在最後一個 GOP 後完成 fragmented writer
- **THEN** writer MUST flush pending video access unit 與最後一個完整 `moof`/`mdat`，且 MUST NOT 產生空 fragment

### Requirement: Fragmented track freezing, ordering, and buffering
Fragmented mode MUST 在第一個 media sample 前至少具備 video configuration，且 MUST 將當時已設定的 supported tracks 寫入 initial `moov` 後凍結。第一個 video access unit MUST 是 keyframe；跨 video/audio tracks 的 submitted DTS MUST 全域非遞減；單一未完成 fragment 的 buffered bytes MUST 受 options 中正值上限約束。Writer MUST 在提交不一致 fragment 前拒絕 audio-only、late configuration、non-keyframe start、cross-track DTS regression 或 buffer overflow。

#### Scenario: Freeze track configuration
- **WHEN** fragmented writer 已接受第一個 media sample，caller 隨後嘗試新增或變更 video/AAC configuration
- **THEN** writer MUST 拒絕變更，且已寫出的 initial `moov` MUST 保持不變

#### Scenario: Reject non-keyframe start
- **WHEN** fragmented recording 的第一個 video access unit 不是 keyframe
- **THEN** writer MUST 在寫出第一個 media fragment 前拒絕該 sample

#### Scenario: Reject cross-track DTS regression
- **WHEN** fragmented caller 在較晚的 global DTS 後提交較早 DTS 的另一軌 sample
- **THEN** writer MUST 拒絕該 sample，並指出 track 與 timestamp ordering violation

#### Scenario: Bound a fragment without a new keyframe
- **WHEN** buffered fragment 在下一個 keyframe 前將超過 configured maximum fragment buffer bytes
- **THEN** writer MUST 在超過上限前明確失敗，且 MUST NOT 自動輸出從 non-keyframe 開始的新 fragment

#### Scenario: Reject audio-only fragmented output
- **WHEN** caller 嘗試在沒有 video configuration 的 fragmented writer 寫入 AAC media
- **THEN** writer MUST 在寫出 initial metadata 或 media bytes 前拒絕操作

### Requirement: Payload-efficient video ingestion
Writer SHALL以指向`EncodedVideoNalUnit`已擁有private buffer的validated byte ranges處理raw與Annex-B NAL input，而不得為normalization建立第二份payload-sized NAL arrays。Progressive、faststart與fragmented modes MUST維持NAL order、同PTS/DTS aggregation、length-prefix encoding、timestamp validation、keyframe state與caller mutation isolation。

#### Scenario: Normalize a raw NAL without copying its payload
- **WHEN** caller提交已由`EncodedVideoNalUnit`擁有的大型raw H.264或H.265 NAL
- **THEN** Writer MUST以涵蓋既有private buffer的單一validated range表示該NAL，且MUST NOT為normalization配置另一份NAL-sized array

#### Scenario: Split Annex-B without copying NAL payloads
- **WHEN** caller提交含三或四位元組start codes及多個NAL units的Annex-B sample
- **THEN** Writer MUST以來源順序產生排除start codes的validated ranges，拒絕既有invalid/empty cases，且MUST NOT為每個NAL配置payload array

#### Scenario: Preserve access-unit aggregation bytes
- **WHEN** caller以相同PTS/DTS提交多個contiguous NAL units並完成任一writer mode
- **THEN** output MUST與最佳化前固定fixture byte-identical，且Reader round-trip MUST回傳相同NAL boundaries、timing與keyframe state

#### Scenario: Reject normalization without partial state
- **WHEN** invalid Annex-B boundaries、empty NAL或length overflow使normalization拒絕submitted sample
- **THEN** Writer MUST NOT保留該sample的任何range、改變buffer/timestamp state或寫出該sample，且後續finalization MUST只包含先前已接受samples

### Requirement: Payload-efficient fragmented buffering
Fragmented Writer MUST以contiguous AAC payload或video NAL ranges保存pending fragment data，並以實際length-prefix加payload bytes計入既有fragment buffer limit。它MUST NOT為每個video access unit materialize完整的第二份length-prefixed payload，且成功flush後MUST釋放該fragment對payload backing arrays的references。

#### Scenario: Buffer a GOP without materializing video access units
- **WHEN** fragmented Writer接受由keyframe、multiple-NAL non-keyframes與下一個keyframe界定的GOP
- **THEN** pending fragment MUST保存owned payload ranges與checked encoded sizes，MUST NOT保存每個video sample的第二份完整encoded payload，且retained encoded bytes MUST NOT超過configured limit

#### Scenario: Flush ranged payloads sequentially
- **WHEN**下一個keyframe或finalization觸發fragment flush至writable non-seekable stream
- **THEN** Writer MUST依既有video-first、audio-second payload order寫入相同`moof`/`mdat` bytes，並在成功後移除已提交sample references與精確扣除buffered byte count

#### Scenario: Preserve fragment buffer rejection boundary
- **WHEN**加入下一個NAL range或AAC payload將使length-prefix加payload總數超過`MaximumFragmentBufferBytes`
- **THEN** Writer MUST在寫出不一致fragment前以既有error semantics拒絕operation，MUST NOT保留rejected ranges或推進timestamp state，且MUST NOT因range representation少算或重複計算logical encoded bytes

#### Scenario: Preserve state after fragment output failure
- **WHEN** output Stream在`moof`、`mdat` header或range payload write期間拋出exception
- **THEN** Writer MUST NOT提前移除selected samples、扣除logical buffered bytes或推進fragment sequence state，且MUST維持既有partial-output failure semantics

### Requirement: Single-build fragment metadata
Fragmented Writer SHALL在一次邏輯`moof` build中保留`trun.data_offset` patch positions，並在box length確定後以checked big-endian writes原地解析video與audio offsets。Writer MUST以checked conservative capacity預先配置常態buffer，MUST NOT為相同fragment建立provisional與final兩份完整`moof`，且patch MUST NOT改變box length或其他metadata bytes。

#### Scenario: Resolve mixed-track data offsets in one buffer
- **WHEN** fragment同時包含多個video與AAC samples及composition offsets
- **THEN** Writer MUST只執行一次final `moof` logical build、將video offset指向`mdat`第一個video payload、將audio offset指向video payload之後，且output MUST與既有fixture byte-identical

#### Scenario: Reject an unrepresentable patched offset
- **WHEN** computed `trun.data_offset`無法以required signed 32-bit field表示或patch position不在`moof` boundary內
- **THEN** Writer MUST在提交fragment前拋出可診斷exception，且MUST NOT寫出部分patched metadata

### Requirement: Additive asynchronous Writer API
`Mp4Writer` SHALL保留所有既有constructors、同步methods與aliases，並 SHALL新增具正體中文 XML documentation 的dependency-free `netstandard2.0` async factories、`WriteVideoNalUnitAsync`、`WriteAudioSampleAsync`及`FinalizeFileAsync`，每個async member MUST接受最後一個optional `CancellationToken`。Async implementation MUST使用caller Stream的byte-array `ReadAsync`/`WriteAsync` overload與`ConfigureAwait(false)`，MUST NOT使用`Task.Run`或sync-over-async；`Position`、`Length`、`Seek`、`SetLength`及internal MemoryStream metadata building MAY保持同步。Async completion MUST NOT額外承諾`FlushAsync`或durable storage。

#### Scenario: Create every Writer mode without synchronous external writes
- **WHEN** caller以同步`Write`會失敗而`WriteAsync`可完成的合法Stream呼叫任一`CreateAsync` overload
- **THEN** progressive與faststart factory MUST以async external write完成既有header，fragmented factory MUST依既有lazy-start semantics保持zero output，且任何mode都MUST snapshot options並維持既有capability validation

#### Scenario: Reject invalid async creation before a successful header
- **WHEN** caller提供null Stream、invalid options或mode不支援的Stream capability
- **THEN** async factory MUST以既有exception category拒絕、MUST NOT宣稱建立Writer，且MUST NOT因`leaveOpen:false`關閉caller-owned Stream

#### Scenario: Fail during asynchronous factory header output
- **WHEN**progressive或faststart `CreateAsync`在header `WriteAsync`中途取消或收到exception
- **THEN**factory task MUST呈現cancellation或failure、MUST NOT回傳Writer、physical output MAY不完整、MUST NOT因`leaveOpen:false`關閉caller-owned Stream，且caller MUST負責丟棄或恢復output

#### Scenario: Preserve the existing synchronous surface
- **WHEN** caller只使用既有constructors、configure methods、sync write methods與finalization aliases
- **THEN** public signatures、default progressive mode、blocking behavior、stream ownership及MP4 output MUST維持pre-change結果，且sync path MUST NOT透過等待async Task實作

### Requirement: Asynchronous Writer output for every layout
Async Writer path MUST讓progressive payload output、fragmented initial metadata與fragment flush、faststart relocation及所有layout finalization的caller Stream payload I/O使用cancellable async virtual methods。Metadata MAY先在internal memory同步建立，但external async call count MUST以metadata block、sample或NAL range數量為界，MUST NOT隨payload byte length逐byte增加。Pure async與sequential mixed sync/async submissions MUST在H.264/H.265及progressive、faststart、fragmented modes產生與既有sync path byte-identical的MP4，並維持configuration、timing、ordering、resource limits及error semantics。

#### Scenario: Write and finalize progressive output asynchronously
- **WHEN** caller以async factory建立progressive Writer，依序await configured H.264/H.265 video及AAC sample writes後await finalization
- **THEN** header、audio payload、pending video length prefixes/ranges、mdat backpatch與moov append MUST透過async external writes完成，且output MUST與相同sync fixture byte-identical

#### Scenario: Relocate faststart media asynchronously with bounded memory
- **WHEN** caller以async path完成faststart fixture
- **THEN** Writer MUST保留同步position/SetLength controls與固定64 KiB relocation buffer、以`ReadAsync`/`WriteAsync`向後搬移mdat、產生`ftyp`/`moov`/`mdat` layout，且MUST NOT對caller Stream呼叫同步`Read`或`Write`

#### Scenario: Flush fragmented output to an async non-seekable Stream
- **WHEN** caller以writable non-seekable async Stream提交以keyframe開始的video、AAC、non-keyframe及下一個keyframe
- **THEN** Writer MUST async輸出initial `ftyp`/`moov`與keyframe-aligned `moof`/`mdat` fragments，只在完整fragment output成功後移除samples、扣除buffered bytes與推進sequence，且final output MUST與sync fixture byte-identical

#### Scenario: Mix sync and async calls sequentially
- **WHEN** caller在沒有operation overlap且Writer未fault的情況下依序混用sync/async sample writes與任一sync/async finalization入口
- **THEN** Writer MUST共用一致的media state、維持submission order，且成功後`FinalizeFile()`、`FinalizeFileAsync()`、`Complete()`與`Finish()`交叉重複呼叫MUST保持idempotent

#### Scenario: Bound asynchronous external write amplification
- **WHEN** async Writer輸出large single-NAL、multi-NAL與tiny-NAL deterministic fixtures
- **THEN** recorded async external calls MUST符合文件化的bounded metadata/header/payload模型、sync external call count MUST為零，且call count MUST NOT隨單一payload的byte length成長

### Requirement: Deterministic Writer overlap, cancellation, and fault state
同一`Mp4Writer`一次 MUST最多執行一個stateful configure、write、finalize或dispose operation。Overlap MUST fail-fast而不得排隊、不得改變active operation或media state。對已成功回傳的Writer，async method MUST依序檢查Disposed、Faulted、Finalized idempotent finalize、operation gate、arguments/operation legality及cancellation；合法Idle operation的already-cancelled token MUST在external I/O與state mutation前取消且保持Writer可用。Validation、format、timestamp或resource rejection若發生於本次output-risk boundary前，也 MUST保持既有可恢復性。一旦新增async operation開始external write或可能改變output/安全重試位置的`SetLength`、Seek/Position/backpatch/relocation control，之後的cancellation或任何exception MUST將Writer標記為terminal Faulted、指出output可能不完整，並 MUST讓後續所有sync/async configure、write及finalize methods/aliases以`InvalidOperationException`失敗；無active operation的Dispose仍 MUST依`leaveOpen`完成。最後一個async I/O成功後Writer MUST不再作late cancellation check，而 MUST在operation gate內commit並成功。

#### Scenario: Reject async and sync overlap without harming the active operation
- **WHEN**第一個async write停在deterministic delayed Stream gate，caller同時發出第二個async write或sync configure/write/finalize/dispose
- **THEN**重疊operation MUST立即以`InvalidOperationException`拒絕、MUST不改變payload/timestamp/fragment state，且第一個operation在gate釋放後 MUST仍能正常完成

#### Scenario: Cancel before external output
- **WHEN**caller使用already-cancelled token呼叫async create、write或finalize，且該operation尚未接觸caller Stream
- **THEN**在arguments及instance/operation legality有效、Writer尚未finalized的情況下task MUST呈現cancellation、output與Writer state MUST不變，且既有Writer MUST仍能接受後續合法operation

#### Scenario: Fault after crossing the output-risk boundary
- **WHEN**已成功回傳的Writer在progressive、fragmented或faststart async operation開始external output、`SetLength`、Seek/Position backpatch或relocation control後，被取消或收到任意exception
- **THEN**physical output或position MAY不完整，但Writer MUST NOT把未完整operation提交為成功、MUST進入Faulted、後續sync/async configure/write/finalize methods與aliases MUST以指出output可能不完整的`InvalidOperationException`拒絕，且Dispose MUST仍遵守`leaveOpen`

#### Scenario: Preserve pre-output rejection recoverability
- **WHEN**async sample因invalid configuration、NAL boundary、timestamp order或fragment buffer limit在本次external output前被拒絕
- **THEN**Writer MUST不進入Faulted、不保留rejected sample或推進state，並 MUST能接受後續合法sample

#### Scenario: Apply deterministic state and cancellation precedence
- **WHEN**caller以cancelled token分別呼叫disposed、Faulted、Active或Finalized Writer，或以null sample呼叫合法Idle Writer
- **THEN**disposed MUST維持`ObjectDisposedException`、Faulted與Active MUST為`InvalidOperationException`、Finalized的finalize MUST成功idempotent完成、null argument MUST為`ArgumentNullException`，且只有其餘合法Idle operation MUST呈現cancellation

#### Scenario: Commit after the final asynchronous I/O succeeds
- **WHEN**token在entry時有效且所有async I/O成功，但在最後一個I/O完成後、state commit前才被signal
- **THEN**Writer MUST不執行late cancellation check、MUST原子commit並成功完成operation，且output與state MUST一致

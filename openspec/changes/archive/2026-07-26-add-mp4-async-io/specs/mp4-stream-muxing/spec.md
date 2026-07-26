## ADDED Requirements

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

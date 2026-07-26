## ADDED Requirements

### Requirement: Asynchronous Reader snapshot construction
`Mp4Reader` SHALL 提供 dependency-free `netstandard2.0` 相容且具正體中文 XML documentation 的 `CreateAsync(Stream, bool, CancellationToken)` factory。Factory MUST 使用 caller Stream 的 cancellable `ReadAsync(byte[], int, int, CancellationToken)` 建立與同步 constructor相同的完整 snapshot，並 MUST 保持 readable/seekable capability、256 MiB input limit、codec discovery、error、event、defensive-copy、repeatable enumeration與stream ownership semantics。Snapshot完成後的 parsing及 `ReadVideoNalUnits()`、`ReadAudioSamples()`、`Read()`與aliases SHALL維持同步 memory-only operations，且 implementation MUST NOT以`Task.Run`或async enumeration包裝它們。

#### Scenario: Create a Reader through true async snapshot I/O
- **WHEN** caller以同步`Read`會失敗、`ReadAsync`會延遲完成的readable/seekable Stream呼叫`CreateAsync`
- **THEN** returned task MUST在async reads完成前保持未完成，factory MUST只透過`ReadAsync`取得input bytes，並 MUST建立與同步constructor具有相同configuration、payload、timing及events的Reader

#### Scenario: Restore a nonzero input position
- **WHEN** caller從nonzero original position對有效MP4呼叫`CreateAsync`
- **THEN** factory MUST從stream起點snapshot完整MP4，並在成功後將Stream恢復至原始position

#### Scenario: Cancel before or during snapshot
- **WHEN** token在第一個Stream access前已取消，或在delayed `ReadAsync` loop期間取消
- **THEN** factory task MUST呈現cancellation、MUST在finally嘗試恢復original position、restore成功時MUST回到原位置、MUST NOT回傳partial Reader或關閉caller Stream，且pre-cancel MUST NOT讀取input

#### Scenario: Preserve asynchronous factory failure semantics
- **WHEN** async input宣告過大length、提前結束、缺少required capability、在read/position restore期間失敗或包含既有parser會拒絕的malformed MP4
- **THEN** factory MUST維持對應同步path的resource guard與exception category、MUST NOT輸出partial samples，且在未成功回傳Reader前MUST NOT因`leaveOpen:false`關閉caller Stream；若read/cancellation與finally restore同時失敗，restore exception MUST依既有同步finally語意優先

#### Scenario: Keep post-construction delivery synchronous and repeatable
- **WHEN** caller成功await `CreateAsync`後重複使用任一既有Reader delivery entry point
- **THEN** delivery MUST不再存取input Stream，並 MUST維持既有event order、repeatable enumeration、defensive-copy及Reader dispose後sample lifetime

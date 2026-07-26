# add-mp4-async-io test matrix

證據來源對應 delta spec scenarios 與 tasks.md。每個 case 標記證據類型與 expected sync/async Stream calls。

## Reader (`mp4-stream-demuxing`)

| Scenario | Evidence | Sync calls | Async calls |
| --- | --- | --- | --- |
| True async snapshot I/O | unit `AsyncApiContractTests.ReaderCreateAsyncUsesAsyncSnapshotAndReturnsReader` / `WriterAsyncTests` | 0 sync read | ≥1 `ReadAsync` |
| Nonzero position restore | unit `ReaderCreateAsyncRestoresNonzeroPosition` | 0 | `ReadAsync` loop; `Seek` in finally |
| Pre-cancel / mid-read cancel | unit `ReaderCreateAsyncPreCancelDoesNotReadStream` / `ReaderCreateAsyncMidReadCancelRestoresPositionAndDoesNotReturnReader` | pre-cancel 0 read; mid-read cancel returns cancelled task | `ReadAsync` cancelled |
| Factory failure / ownership (`leaveOpen:false`) | unit `ReaderCreateAsyncFactoryFailureDoesNotCloseCallerStreamWhenLeaveOpenFalse` | 0 | not closed on failure |
| Post-construction zero Stream I/O | unit `AsyncRoundTripThroughReaderMatchesSyncRoundTrip` (sync delivery after async factory) | delivery 0 Stream I/O | snapshot only |
| File-backed async snapshot | integration `AsyncWriterReaderMatrixPreservesPayloadTimingAndConfiguration` | 0 sync read | `FileOptions.Asynchronous` |

## Writer factory & state (`mp4-stream-muxing`)

| Scenario | Evidence | Sync calls | Async calls |
| --- | --- | --- | --- |
| Async header (progressive/faststart) | unit `CreateAsyncProgressiveWritesHeaderViaAsyncWritesOnly` | 0 sync write | ≥1 `WriteAsync` |
| Fragmented zero-output lazy start | unit `CreateAsyncFragmentedEmitsZeroOutput` | 0 | 0 |
| Invalid capability/options | unit `CreateAsyncRejectsNonWritableBeforeAnyHeaderAndDoesNotClose` | 0 | not closed |
| Mid-header cancel | unit `CreateAsyncMidHeaderCancelDoesNotReturnWriterAndDoesNotCloseCaller` | 0 | cancelled; no instance |
| Overlap (sync/async, async/async) | unit `OverlappingSyncWrite...` / `OverlappingAsyncWrite...` | rejected 0 state change | first completes |
| Pre-cancel vs Faulted/Active/Finalized/disposed precedence | unit `WriterAsyncCancellationTests` | precedence asserted | pre-cancel keeps usable |
| Output-risk Faulted | unit `FaultedWriterRejectsAllSubsequentOperationsExceptDispose` etc. | Faulted rejects all | mid-I/O cancel/exception → Faulted |
| Late-cancel commit | design guarantee (no late `ThrowIfCancellationRequested`) | — | commit after final I/O |
| Factory failure ownership | unit `CreateAsync*DoesNotClose` | not closed on failure | — |

## Writer output per layout

| Scenario | Evidence | Sync calls | Async calls |
| --- | --- | --- | --- |
| Progressive async byte-identical | unit `ProgressiveAsyncOutputIsByteIdenticalToSyncOutput` / `ProgressiveAsyncVideoAndAudioIsByteIdenticalAndRoundTrips` | 0 sync write in async path | bounded per NAL/sample |
| Fragmented async (seekable + non-seekable) | unit `FragmentedAsyncOutputIsByteIdenticalToSyncAndRoundTrips` / `FragmentedAsyncOnNonSeekableStreamMatchesSyncOutput` | 0 sync write | `moof`/`mdat` async |
| Faststart async relocation | unit `FastStartAsyncOutputIsByteIdenticalToSyncOutput` / `FastStartRelocationFailureFaultsWriter` | sync `SetLength`/Seek control; 0 sync read/write | `ReadAsync`/`WriteAsync` 64 KiB relocation |
| Sequential mixed sync/async | unit `SequentialMixedSyncAsyncProducesIdenticalProgressiveOutput` | mixed legal | mixed legal |
| Cross-sync/async finalization idempotent | unit `FinalizedWriterAsyncFinalizeIsIdempotent` / `FinalizeFileAsyncWithAlreadyCancelledTokenStillCompletesWhenFinalized` | idempotent | idempotent |
| Bounded external write amplification | benchmark `async.writer.*` scenarios | 0 sync fallback | calls bounded by NAL/sample count |

## Console (`mp4-component-verification`)

| Scenario | Evidence | Sync/async |
| --- | --- | --- |
| async H.264/H.265 × 3 layouts | integration `ConsoleAsyncModeWritesReopensAndPrintsAsyncMarker` | `I/O: async`, 3 video/4 AAC, Reader round-trip |
| default/explicit sync compatibility | integration `ConsoleDefaultAndExplicitSyncPrintsSyncMarker` | `I/O: sync` additive |
| unknown I/O mode | integration `ConsoleRejectsUnknownIoModeWithoutCreatingSuccessOutput` | nonzero exit, no output |
| ffprobe/ffmpeg validation | integration `AsyncGeneratedLayoutPassesFfprobeAndFfmpegValidation` + manual probe | hevc/aac, no errors |

## Integration matrix

| Scenario | Evidence |
| --- | --- |
| Six codec/layout async round-trip | integration `AsyncWriterReaderMatrix...` |
| Sync/async byte-identical SHA-256 | integration `AsyncOutputIsByteIdenticalToSyncOutput` |
| Async file-backed + ffprobe/ffmpeg | integration `AsyncGeneratedLayoutPassesFfprobeAndFfmpegValidation` |

## Benchmark (`mp4-component-verification`)

| Family | Scenarios | Evidence |
| --- | --- | --- |
| Immediate-completion memory | `async.reader.snapshot.*`, `async.writer.ingestion.progressive.*`, `async.writer.fragment-flush.*`, `async.writer.finalize.faststart.*` | `self-test` + smoke capture; sync completion ratio ~1; 0 sync fallback |
| Real file-backed | `async.reader.snapshot.file.*`, `async.writer.ingestion.file.*` | `FileOptions.Asynchronous`; setup excluded |
| Bounded-concurrency gated | `async.concurrency.{reader,writer}.{1,32,128}.h264` | max outstanding == concurrency; 0 sync fallback; completed == concurrency |

## Documentation (`mp4-component-verification`)

| Scenario | Evidence |
| --- | --- |
| Marked sync/async C# snippets compile | unit `ReadmeSnippetTests` (Roslyn) |
| Async contract documented | README async section |
| Compatibility baseline additive-only | benchmark `baseline` (6 async signatures, fixed-output unchanged) |
## 1. Solution and public-contract baseline

- [ ] 1.1 Create a solution with a .NET Standard 2.0 library, xUnit unit-test project, xUnit integration-test project, and .NET 10 Console project; add project references and deterministic test configuration.
- [ ] 1.2 Define the public codec, sample, timestamp, event-args, ownership, and diagnostic-exception contracts, including H.264 SPS/PPS, H.265 VPS/SPS/PPS, AAC AudioSpecificConfig, PTS/DTS/duration, and seekable-stream validation.
- [ ] 1.3 Write failing unit tests for public input validation, stream ownership, timestamp conversion precision, decreasing-DTS rejection, and codec-configuration completeness.
- [ ] 1.4 Implement only the contract validation and immutable media models needed to make the baseline unit tests pass.

## 2. MP4 primitives and test-first writer foundation

- [ ] 2.1 Write failing unit tests for big-endian primitive encoding, box length/backpatching, four-character box types, and 32-bit versus 64-bit chunk-offset selection.
- [ ] 2.2 Implement the internal ISO BMFF binary writer and box-scope helpers required by those tests.
- [ ] 2.3 Write failing unit tests for H.264/H.265 Annex-B-to-length-prefixed NAL handling, equal-PTS/DTS NAL aggregation, final access-unit flush, AAC sample capture, and stable sample ordering.
- [ ] 2.4 Implement video NAL aggregation and AAC sample collection without emitting MP4 metadata until the unit tests pass.
- [ ] 2.5 Write failing unit tests for `ftyp`/`mdat`, `avcC`, `hvcC`, `esds`, movie/track headers, `stts`, `stsc`, `stsz`, `stco`/`co64`, `stss`, and `ctts` creation from known samples.
- [ ] 2.6 Implement progressive MP4 finalization to a caller-owned seekable stream and make the writer unit tests pass.

## 3. MP4 reader and event delivery

- [ ] 3.1 Write failing unit tests for safe MP4 box traversal, supported-track selection, and parsing `avcC`, `hvcC`, and `esds` into public codec configurations.
- [ ] 3.2 Implement MP4 metadata and sample-table parsing with diagnostic format errors for malformed, unsupported, or inconsistent input.
- [ ] 3.3 Write failing unit tests that split length-prefixed video samples back into ordered NAL units and reconstruct PTS, DTS, duration, and key-frame state from sample tables.
- [ ] 3.4 Implement timed video sample/NAL enumeration and synchronous `VideoNalUnitRead` event delivery.
- [ ] 3.5 Write failing unit tests for timed AAC access-unit enumeration, parsed AAC parameters, AAC events, handler exception propagation, and caller-stream ownership.
- [ ] 3.6 Implement timed AAC enumeration and synchronous `AacSampleRead` event delivery, then make all reader unit tests pass.

## 4. Interoperability integration tests

- [ ] 4.1 Add version-controlled, minimal legal H.264, H.265, and AAC fixture data plus a shared helper that creates temporary MP4 files through the public writer API.
- [ ] 4.2 Write integration tests that round-trip H.264/AAC and H.265/AAC through the public writer and reader, comparing every configuration byte array, media payload, PTS, DTS, duration, and key-frame value.
- [ ] 4.3 Write integration tests that resolve `ffprobe` and `ffmpeg` from PATH, report missing tools as an explicit prerequisite result, and invoke `ffprobe -show_format -show_streams` to assert MP4 container and expected codec streams.
- [ ] 4.4 Extend integration tests to invoke `ffmpeg -v error -i <file> -map 0 -f null -` for each generated fixture and fail with captured, redacted tool diagnostics on decode or demux errors.
- [ ] 4.5 Run the unit and integration test projects; retain the exact commands and concise pass/fail evidence in the change results before completing this group.

## 5. Console demonstration and documentation

- [ ] 5.1 Write a failing Console smoke/integration test or deterministic harness expectation for the end-to-end write-file, reopen-file, event-subscribe, and timestamp-print workflow.
- [ ] 5.2 Implement the .NET 10 Console demo to write an MP4 to a caller-specified path, reopen it, subscribe to video/AAC events, and print all parameter sets, AAC parameters, sample sizes, and timestamps.
- [ ] 5.3 Document supported codecs, required seekable `Stream` semantics, NAL aggregation rule, AAC input contract, exception behavior, and exact build/test/ffprobe/ffmpeg commands.
- [ ] 5.4 Execute `dotnet test` for the full solution, execute the Console demo against an output path, and run ffprobe/ffmpeg against that output as final acceptance evidence.

## 6. Final review

- [ ] 6.1 Review public APIs for .NET Standard 2.0 compatibility, null/input validation, stream ownership, and absence of native runtime dependencies.
- [ ] 6.2 Review the final diff for minimal scope and verify all OpenSpec requirements have a corresponding passing unit or integration test.

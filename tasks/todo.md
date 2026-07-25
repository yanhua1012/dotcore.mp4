# 2026-07-25 Implement `add-netstandard-mp4-component`

## Acceptance criteria

- [x] A .NET Standard 2.0 library exposes validated H.264/H.265/AAC contracts, a progressive writer, and a reader with timed events.
- [x] Unit tests cover contract validation, ISO BMFF primitives, muxing, demuxing, timestamp/error/ownership paths independently of ffmpeg.
- [x] Integration tests round-trip H.264/AAC and H.265/AAC and verify generated files with PATH-resolved `ffprobe` and `ffmpeg`.
- [x] A .NET 10 Console demo writes, reopens, subscribes to events, and prints codec parameters and timestamps.
- [x] `dotnet test` and the final Console/ffprobe/ffmpeg acceptance commands have passing evidence.

## Checkpoints

- [x] A — inspect OpenSpec context and establish the baseline.
- [x] B — implement public contracts, primitives, writer, and targeted unit coverage.
- [x] C — implement reader, integration fixtures/tests, Console demo, and documentation.
- [x] D — run full verification, review scope, and record results.

## Dependencies and environment

- .NET SDK 10.0.203; library target is `netstandard2.0`.
- `ffprobe` and `ffmpeg` must be available on `PATH` for interoperability tests.
- The production library has no native runtime or external-tool dependency.

## Working notes

- H.265 is included because VPS is explicitly required; H.264 uses SPS/PPS only.
- Version one writes a progressive, non-fragmented MP4 to a caller-owned seekable stream and rejects non-seekable output.
- Public timestamps use exact `TimeSpan` conversion; no silent rounding is allowed.
- Same PTS/DTS contiguous video NALs form one access unit; DTS must not decrease within a track.

## Results

- `dotnet build DotCore.Mp4.sln --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true` — passed, 0 warnings, 0 errors.
- `dotnet test DotCore.Mp4.sln --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true --logger "console;verbosity=minimal"` — passed, 16 unit + 5 integration tests.
- `dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-demo-final.mp4` — exit 0; printed codec parameters, two video NALs, and two AAC samples.
- `ffprobe -v error -show_format -show_streams -of json /tmp/dotcore-demo-final.mp4` — identified `h264` and `aac` streams.
- `ffmpeg -v error -i /tmp/dotcore-demo-final.mp4 -map 0 -f null -` — exit 0, stderr 0 bytes.
- `git diff --check` — passed; production dependency audit found only automatic `NETStandard.Library`, with no native runtime/tool invocation.

# 2026-07-25 Verify `add-netstandard-mp4-component`

## Acceptance criteria

- [x] All 27 implementation tasks have corresponding current-checkout outputs; historical test-first ordering remains unprovable.
- [x] Every OpenSpec requirement and scenario maps to implementation/test evidence, including explicitly recorded partial coverage and one behavior divergence.
- [ ] The implementation follows the documented design without material issues; codec NAL-type validation and malformed-AAC exception semantics diverge.
- [x] OpenSpec validation, solution build/tests, Console round-trip, ffprobe, and ffmpeg pass from the current checkout.
- [x] The final report identifies all critical, warning, and suggestion findings with actionable file references.

## Checkpoints

- [x] A — load status, apply instructions, and every planning artifact.
- [x] B — map tasks, requirements, and scenarios to current implementation/tests.
- [x] C — verify design coherence and run deterministic acceptance commands.
- [x] D — record results and issue the archive-readiness assessment.

## Risk and rollback

- Risk level: low; this activity verifies existing behavior and does not change production code.
- Affected components: OpenSpec artifacts, production library, unit/integration tests, Console demo, and README.
- Rollback: revert only this verification-notes section if it is not wanted.

## Dependencies and environment

- Expected SDK: .NET 10; production target: `netstandard2.0`.
- `ffprobe` and `ffmpeg` must be available on `PATH`.
- Verification must preserve unrelated untracked files and must not rely on prior build output alone.

## Working notes

- Schema: `spec-driven`; source of truth is the repo-local change.
- Artifact inventory: proposal, design, three delta specs, and tasks.
- Objective checklist baseline: 27/27 tasks marked complete.

## Results

- Completeness: 27/27 tasks checked; implementation evidence found for 11/11 requirements.
- Correctness: 10/11 requirements conform; Console prints pre-write codec configuration rather than reader-parsed configuration. Of 15 scenarios, 9 have strong direct tests, 5 have partial tests, and 1 is affected by the Console divergence.
- Coherence: the managed-only `netstandard2.0` architecture and core mux/demux design are followed; codec-specific parameter-set NAL types are not validated and malformed AAC configuration can escape the reader as `ArgumentException`.
- `openspec validate add-netstandard-mp4-component --strict --json` — passed, 1/1 change valid.
- Initial no-restore build — failed before compilation because stale NuGet assets referenced `C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages`.
- `dotnet restore DotCore.Mp4.sln /p:RestoreFallbackFolders= /p:RestorePackagesPath=/root/.nuget/packages` — restored all four projects.
- `dotnet build DotCore.Mp4.sln --no-restore /p:RestoreFallbackFolders= /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true` — passed, 0 warnings, 0 errors.
- `dotnet test DotCore.Mp4.sln --no-restore /p:RestoreFallbackFolders= /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true --logger "console;verbosity=minimal"` — passed, 16 unit + 5 integration tests, 0 skipped.
- Console demo wrote `/tmp/dotcore-mp4-verify-20260725.mp4`, printed two video and two AAC events; `ffprobe` identified H.264/AAC MP4, and `ffmpeg` completed with exit 0 and no error diagnostics.
- Final assessment: no CRITICAL issues; warnings should be resolved or explicitly accepted before archive.

# 2026-07-25 Remediate first five verification warnings

## Acceptance criteria

- [x] Console codec output comes from the reopened reader and is asserted exactly.
- [x] H.264/H.265 parameter-set NAL types are rejected when mislabeled.
- [x] Malformed AAC metadata fails as `Mp4FormatException` with an actionable diagnostic.
- [x] Reader input size, expanded sample count, and descriptor nesting are deterministically bounded.
- [x] All five partially covered scenario groups have direct regression tests.
- [x] OpenSpec strict validation, full build/tests, Console, ffprobe, and ffmpeg pass.

## Checkpoints

- [x] A — reload OpenSpec context and convert warnings into remediation tasks.
- [x] B — add failing regression tests and preserve failure evidence.
- [x] C — implement the smallest compatible fixes.
- [x] D — run full verification and review archive readiness.

## Risk and rollback

- Risk level: medium; parser rejection behavior becomes stricter for malformed or resource-exhausting input.
- Affected components: public codec configuration construction, MP4 reader error semantics, Console output, and unit/integration tests.
- Backward compatibility: valid H.264/H.265/AAC files and public API shapes must remain unchanged.
- Rollback: revert the remediation diff; no schema, persistent data, or external state migration is involved.
- Monitoring signal: new `Mp4FormatException` diagnostics identify rejected parameter-set type, AAC config, resource count, or descriptor depth.

## Dependencies and environment

- .NET SDK 10.0.203 with production target `netstandard2.0`.
- `ffprobe` and `ffmpeg` available on `PATH`.
- NuGet restore must use `/p:RestoreFallbackFolders=` in this mounted checkout.

## Working notes

- Resource limits must be constants with diagnostics and tests, not timing- or allocation-failure dependent.
- Reader-valid files generated by the writer remain accepted.
- Existing unrelated untracked verification-skill and IDE files remain untouched.
- Failure baseline: 5/26 unit tests and the Console smoke test failed for the expected missing warning-remediation behavior; 21 coverage-only unit tests passed.

## Results

- Failure baseline: 5/26 unit tests and 1/1 Console smoke test failed for missing NAL-type validation, AAC exception normalization, resource guards, and parsed codec output.
- Targeted remediation tests passed for Console output, codec NAL types, malformed AAC/video configuration, input/sample/descriptor limits, sample-table consistency, `co64` overflow, `stsc` ordering, and early duplicate-track rejection.
- `dotnet build DotCore.Mp4.sln --no-restore /p:RestoreFallbackFolders= /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true` — passed, 0 warnings, 0 errors.
- `dotnet test DotCore.Mp4.sln --no-restore /p:RestoreFallbackFolders= /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true --logger "console;verbosity=minimal"` — passed, 30 unit + 5 integration tests, 0 skipped.
- Console demo wrote `/tmp/dotcore-mp4-remediation-final-20260725.mp4` and printed codec configuration obtained from the reopened reader plus two video and two AAC events.
- `ffprobe` identified H.264, AAC, and the MP4 container; `ffmpeg -v error` completed with exit 0 and no diagnostics.
- Security/performance review confirmed bounds occur before large allocations/loops; follow-up review findings were remediated with linear `stsc` lookup, diagnostic `co64` conversion, and duplicate-track preflight.

# 2026-07-25 Archive `add-netstandard-mp4-component`

## Acceptance criteria

- [x] All artifacts are complete and all 32 implementation tasks are checked.
- [x] Three delta specs are synchronized to main specs without requirement or scenario drift.
- [x] The change is moved to `openspec/changes/archive/2026-07-25-add-netstandard-mp4-component`.
- [x] No active OpenSpec changes remain and all main specs pass strict validation.

## Risk and rollback

- Risk level: low; this moves completed planning artifacts and adds synchronized main specs.
- Rollback: move the archived directory back to `openspec/changes/add-netstandard-mp4-component` and remove the three newly synchronized main specs.
- Unrelated `.vs/` content remains untouched.

## Results

- Synchronized 11 requirements and 15 scenarios across `mp4-component-verification`, `mp4-stream-demuxing`, and `mp4-stream-muxing`.
- Requirement/scenario bodies match their delta specs exactly.
- `openspec list --json` — no active changes.
- `openspec validate --all --strict --json` — passed, 3/3 main specs valid.
- Archived task checklist — 32 checked, 0 unchecked.

# 2026-07-25 Propose `add-faststart-fragmented-mp4`

## Acceptance criteria

- [x] Proposal preserves default progressive behavior and scopes faststart plus keyframe-aligned fragmented writer output.
- [x] Design includes `Mp4Reader` fragmented demuxing, Console three-mode round-trip, stream/resource contracts, compatibility, risk, and rollback decisions.
- [x] Delta specs modify muxing, demuxing, and component verification with testable scenarios.
- [x] Tasks are tests-first, independently trackable, and include real test discovery, FFmpeg interoperability, Console, documentation, and final verification.
- [x] OpenSpec reports all apply-required artifacts complete and strict validation passes.

## Risk and rollback

- Risk level: low; this change creates planning artifacts only and does not modify production behavior.
- Affected components for later implementation: writer/reader public contracts and internals, Console, unit/integration tests, fixtures, README, and main specs.
- Rollback: remove `openspec/changes/add-faststart-fragmented-mp4` and this planning note; no runtime or persistent data state is affected.

## Dependencies and environment

- Planning schema: repo-local `spec-driven`.
- Future implementation retains `netstandard2.0` for the library and .NET 10 for tests/Console.
- `ffprobe` and `ffmpeg` remain integration-test prerequisites, not production dependencies.

## Working notes

- Default constructor remains progressive; faststart and fragmented are explicit opt-in modes.
- Fragmented writer requires video, starts fragments at keyframes, bounds one-GOP buffering, and requires cross-track DTS order.
- `Mp4Reader` parses completed snapshots of progressive, faststart, and fragmented files; live tail-follow remains out of scope.
- Console preserves `<output-path>` and adds optional `[progressive|faststart|fragmented]`.

## Results

- Created proposal, design, three delta specs, and a 45-item tests-first task checklist.
- `openspec status --change add-faststart-fragmented-mp4` — 4/4 artifacts complete and apply-ready.
- `openspec validate add-faststart-fragmented-mp4 --strict --json` — passed, 1/1 change valid.

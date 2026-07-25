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

# 2026-07-25 Implement `add-faststart-fragmented-mp4`

## Acceptance criteria

- [ ] Existing `Mp4Writer(Stream, bool)` remains progressive and preserves public ownership/error behavior.
- [ ] Explicit progressive, faststart, and fragmented modes satisfy their stream, layout, timing, buffering, and idempotent-finalization contracts.
- [ ] `Mp4Reader` restores equivalent codec, payload, timing, and keyframe events from all three layouts and fails loudly on malformed/resource-exhausting fragments.
- [ ] Unit, integration, Console, `ffprobe`, `ffmpeg`, OpenSpec strict validation, and diff checks produce non-zero, reproducible evidence.
- [ ] README documents usage, constraints, rollout, and rollback.

## Checkpoints

- [x] A — inspect repository, capture test-discovery baseline, and establish the verification loop.
- [x] B1 — implement and verify public options plus layout-specific stream contracts (OpenSpec 1.1–1.5).
- [x] B2 — implement and verify faststart writer/round-trip (OpenSpec 2.1–2.6).
- [x] B3 — implement and verify fragmented writer boxes/lifecycle (OpenSpec 3.1–4.7).
- [x] C1 — implement and verify fragmented reader metadata/timing/guards (OpenSpec 5.1–6.7).
- [x] C2 — implement and verify integration matrix, FFmpeg references, and Console (OpenSpec 7.1–7.6).
- [x] D — documentation, security/compatibility review, full serialized verification, and scope review (OpenSpec 8.1–8.6).

## Risk and rollback

- Risk level: medium; new layouts and hostile-input parsing expand format behavior, but writer modes are opt-in.
- Affected components: public writer options, writer/reader internals, tests, Console, README, and OpenSpec task state.
- Rollback: revert new options/mode/parser code and related tests/docs; no data migration exists and the legacy progressive constructor remains the compatibility path.
- Monitoring signals: actionable writer capability/order/buffer exceptions and reader `Mp4FormatException` diagnostics; external `ffprobe`/`ffmpeg` validation.

## Dependencies and environment

- Production target remains `netstandard2.0`; tests and Console target .NET 10.
- Restore in this mounted checkout uses `/p:RestoreFallbackFolders=` and a writable NuGet packages path when required.
- `ffprobe` and `ffmpeg` must resolve from `PATH`; they are test/demo prerequisites only.
- Existing untracked `.vs/` content belongs to the user and remains untouched.

## Working notes

- Source of truth: repo-local OpenSpec change `add-faststart-fragmented-mp4`, schema `spec-driven`, 45 tasks.
- Default progressive behavior, public sample/events, managed-only production dependencies, caller stream ownership, and the 256 MiB reader snapshot limit are invariants.
- Fragmented mode requires video, first-keyframe start, globally non-decreasing cross-track DTS, bounded one-fragment buffering, and completed-snapshot reader semantics.
- Any reported task/test/scenario count must come from CLI or `rg`, not manual counting.

## Results

- Baseline before explicit test-project markers: standard `dotnet test DotCore.Mp4.sln` already discovered and passed 30 unit + 5 integration tests; the planned zero-output false green was not reproducible in this checkout.
- Added explicit `<IsTestProject>true</IsTestProject>` to both xUnit projects.
- Post-change `dotnet test DotCore.Mp4.sln --no-restore --logger "console;verbosity=minimal"` passed 30 unit + 5 integration tests, 0 skipped.
- Public mode/options failing baseline: targeted tests failed to compile because `Mp4WriteMode` did not exist.
- Public contract implementation: targeted writer mode tests passed 13/13; complete unit suite passed 43/43, 0 skipped.
- Faststart failing baseline: targeted tests failed to compile because the offset/convergence helper did not exist.
- Faststart implementation: bounded 64 KiB backward relocation, stable moov offset recalculation, `stco/co64` promotion, diagnostic relocation failure, H.264/H.265+Aac reader round-trip, and idempotent completion; full unit suite passed 49/49.
- Fragmented writer failing baseline: all 7 box/lifecycle/resource tests failed against the placeholder mode path.
- Fragmented writer implementation: initial empty-table `moov`/`mvex`, keyframe-aligned `moof`/`mdat`, explicit timing/flags/offsets, track freeze, global DTS guard, bounded buffering, and non-seekable sequential output; targeted tests passed 7/7 and full unit suite passed 56/56.
- Fragmented reader implementation: track-ID/`trex` context, checked `tfhd/tfdt/trun` parsing, cross-track merge, signed composition offsets, `mdat` containment, overlap/sequence/timeline checks, and explicit fragment/traf/trun/sample limits; full unit suite passed 75/75.
- Remaining reader metadata gap before checkpoint completion: add direct multiple-`trun` continuation coverage (OpenSpec 5.2), then close 5.4.
- Added deterministic FFmpeg 6.1.1 H.264/H.265 key/non-key/key fixtures with recorded encoder settings and DTS-interleaved AAC submission.
- Integration matrix covers 6 generated codec/layout combinations plus 2 FFmpeg-generated fragmented references; targeted integration passed 14/14.
- FFmpeg AAC timescale evidence required deterministic reader-side rounding to the nearest 100 ns tick; writer input conversion remains exact-only, and reverse timing differs by at most one `TimeSpan` tick after FFmpeg rescaling.
- Direct two-`trun` continuation and default precedence/first-sample-flags tests close the reader metadata gap.
- Console failing baseline: all 5 mode/compatibility/error smoke cases failed against the one-mode sample.
- Console now preserves output-path-only progressive behavior, supports three explicit modes, interleaves a fixed GOP with AAC, prints parsed reader events, and rejects unknown modes; smoke tests passed 5/5.
- README documents all three layouts, stream contracts, faststart completion semantics, fragmented ordering/buffering constraints, snapshot/resource limits, Console commands, and non-zero test discovery commands.
- Public API review: new mode/options/constructor members have Traditional Chinese XML documentation; production remains `netstandard2.0`, managed-only, and has no added package/native/runtime dependency.
- Security/resource review added checked offset/timing math, explicit fragment/traf/trun/sample limits, `mdat` containment/overlap checks, strict truncated metadata bounds, mutually-exclusive flag validation, and fail-loudly regression tests; no payload or secret logging was introduced.
- Serialized verification: restore passed; solution build passed with 0 warnings/0 errors; unit 77/77 and integration 19/19 passed with 0 skipped; solution-level test discovered and passed both non-zero projects.
- Console progressive, faststart, and fragmented each printed public-reader H.264/AAC codec data plus 3 video/4 audio events; per-file `ffprobe` identified H.264+Aac MP4 and `ffmpeg -v error` exited 0 without diagnostics.
- `openspec validate add-faststart-fragmented-mp4 --strict --json` passed 1/1; `git diff --check` passed.

# 2026-07-26 Archive `add-faststart-fragmented-mp4`

## Acceptance criteria

- [x] The selected change uses the repo-local `spec-driven` schema and all artifacts are complete.
- [x] All implementation tasks are checked with machine-counted evidence.
- [x] Delta specs are assessed against all corresponding main specs and synchronized by explicit user choice.
- [x] The change is moved to `openspec/changes/archive/2026-07-26-add-faststart-fragmented-mp4`.
- [x] OpenSpec reports no active changes and strict validation passes after archival.

## Checkpoints

- [x] A — inspect repository state, lessons, artifact graph, and task completion.
- [x] B — compare delta specs with main specs and synchronize them into main specs.
- [x] C — archive the change and verify the resulting OpenSpec state.

## Risk and rollback

- Risk level: low; archival moves completed planning artifacts, with main-spec edits only if synchronization is required and approved.
- Affected components: repo-local OpenSpec change and possibly the three corresponding main specs.
- Rollback: move the dated archive directory back to `openspec/changes/add-faststart-fragmented-mp4`; revert only any main-spec synchronization performed in this archive step.

## Dependencies and environment

- OpenSpec CLI with nearest repo-local root `/mnt/c/SourceRepository/PublicGitHub/dotcore.mp4`.
- Archive schema: `spec-driven`.

## Working notes

- Artifact status: proposal, design, specs, and tasks are all `done`.
- Task checklist: 51 checked, 0 unchecked.
- Delta capabilities: `mp4-component-verification`, `mp4-stream-demuxing`, and `mp4-stream-muxing`.
- Sync assessment: all three main specs still contain the earlier contracts; the deltas have substantive modifications and additions, no removals or renames, and must preserve unchanged muxing ingestion requirements during merge.

## Results

- Synchronized 16 delta requirement blocks across all three capabilities: 9 modified and 7 added, with no removals or renames.
- Preserved the unchanged muxing requirements for Video codec configuration and NAL ingestion and AAC ingestion and configuration.
- Normalized requirement comparison matched 16/16 delta blocks; pre-archive `openspec validate --all --strict --json --no-interactive` passed 4/4 items.
- Archived to `openspec/changes/archive/2026-07-26-add-faststart-fragmented-mp4`; `.openspec.yaml` is preserved and the archived checklist contains 51 checked, 0 unchecked tasks.
- Post-archive `openspec list --json` reports no active changes; strict validation passes 3/3 main specs.
- `git diff --check` passed.
- Final scope: changes are limited to writer/reader modes and internals, their tests/fixtures, Console, README, explicit test discovery, OpenSpec checklist, and audit notes. Rollout is caller opt-in for new modes; rollback reverts new modes/parser and leaves the legacy progressive format without migration.

# 2026-07-25 Verify `add-faststart-fragmented-mp4`

## Acceptance criteria

- [x] All 45 implementation tasks are objectively complete in the current checkout.
- [ ] All 16 requirements and 44 scenarios map to implementation and regression-test evidence.
- [ ] Implementation follows the documented design and existing repository patterns without material divergence.
- [x] OpenSpec strict validation, build, unit tests, integration tests, solution discovery, Console modes, `ffprobe`, `ffmpeg`, and diff checks have current reproducible evidence.
- [x] Final report classifies every finding as CRITICAL, WARNING, or SUGGESTION with actionable file references.

## Checkpoints

- [x] A — load schema, apply instructions, all planning artifacts, lessons, and repository status.
- [x] B — map tasks, requirements, scenarios, design decisions, implementation, and tests.
- [x] C — run serialized deterministic verification and inspect operational evidence.
- [x] D — record results and issue archive-readiness assessment.

## Risk and rollback

- Risk level: low; this activity verifies existing behavior and does not modify production code.
- Affected components: verification notes only; source, tests, public contracts, and OpenSpec artifacts remain read-only.
- Rollback: revert this verification-notes section if it is not wanted.

## Dependencies and environment

- Schema: repo-local `spec-driven`; change: `add-faststart-fragmented-mp4`.
- Expected SDK: .NET 10; production target: `netstandard2.0`.
- `ffprobe` and `ffmpeg` must resolve from `PATH`.
- Mounted-checkout restore uses `/p:RestoreFallbackFolders=` when required.

## Working notes

- OpenSpec reports proposal, design, three delta specs, and tasks as complete.
- Objective checklist baseline: 45/45 tasks checked.
- Delta-spec inventory: 16 requirements and 44 scenarios, counted by summing `rg -c` results across all three spec files.
- Verification must preserve the clean production checkout and report non-zero per-project test counts.

## Results

- Completeness: 45/45 tasks checked; implementation evidence found for all 16 requirements.
- Correctness: all requirement areas map to code, but Console lacks the specified H.265 sample path and inherited fragment defaults lack a public-reader parser fixture.
- Coherence: architecture, public API compatibility, ownership, mode behavior, managed-only target, and project patterns follow the design; fragment-specific count guards run after generic box-list materialization.
- `openspec validate add-faststart-fragmented-mp4 --strict --json` — passed, 1/1 valid with zero issues.
- Restore completed for 4/4 projects; solution build passed with 0 warnings and 0 errors.
- Unit tests passed 77/77; integration tests passed 19/19; solution discovery passed 96/96; all had 0 failed and 0 skipped.
- PATH-resolved `ffprobe` and `ffmpeg` 6.1.1 ran through the integration suite. Explicit progressive, faststart, and fragmented Console runs each emitted 3 video and 4 AAC events; `ffprobe` identified H.264/AAC MP4 and `ffmpeg -v error` produced zero diagnostic bytes for each file.
- `git diff --check` and `git diff --cached --check` passed.
- Final assessment: 0 CRITICAL, 3 WARNING, 3 SUGGESTION. Resolve or explicitly accept the warnings before archive.

# 2026-07-25 Remediate `add-faststart-fragmented-mp4` verification warnings

## Acceptance criteria

- [x] Console demonstrates fixed H.264/AAC and H.265/AAC GOPs in progressive, faststart, and fragmented modes while preserving the existing output-path-only invocation.
- [x] Public `Mp4Reader` tests cover `trun → tfhd → trex` precedence, `first_sample_flags`, and unresolved defaults.
- [x] Fragment, `traf`, and `trun` limits reject at `limit + 1` during enumeration before generic box-list materialization.
- [x] Targeted failure baselines are recorded before production fixes.
- [x] Full build/tests, Console/external-tool matrix, OpenSpec strict validation, and diff checks pass.

## Checkpoints

- [x] A — convert the three verification warnings into OpenSpec remediation tasks.
- [x] B — add failing regression tests and preserve failure evidence.
- [x] C — implement the smallest compatible fixes and run targeted verification.
- [x] D — run full verification and repeat archive-readiness review.

## Risk and rollback

- Risk level: medium; Console gains an additive codec selector and hostile-input parsing rejects earlier.
- Affected components: Console CLI/sample fixture, fragmented reader box enumeration, unit/integration tests, README, and OpenSpec task records.
- Backward compatibility: existing `<output-path> [mode]` Console invocations and all valid MP4 inputs must retain behavior.
- Rollback: revert this remediation diff; no data migration or persistent external state is involved.

## Dependencies and environment

- .NET SDK 10.0.203; production remains `netstandard2.0`.
- `ffprobe` and `ffmpeg` 6.1.1 are available on `PATH`.
- Dotnet commands remain serialized; restore uses `/p:RestoreFallbackFolders=` when required.

## Working notes

- Tests must use public `Mp4Reader` for inherited-default scenarios, not only `FragmentDefaultsResolver`.
- Early-limit implementation must preserve the generic 100,000-box guard for unrelated containers.
- Console codec selection must be additive and keep output-path-only progressive H.264 behavior.

## Results

- Failure baseline: public-reader inherited-default coverage passed 6/6, confirming a coverage-only warning; the three early-limit tests failed with `incomplete header` instead of specific limit diagnostics; Console smoke failed 7/9 cases against the H.264-only two-argument implementation.
- Targeted remediation: public-reader defaults plus early-limit tests passed 9/9; expanded Console smoke passed 9/9 after adding the compatible codec selector and H.265 fixture.
- Independent review found no production correctness/security defects. Its three coverage warnings were closed by making every defaults precedence value distinguishable, asserting every Console codec-parameter and event-output line, and preserving the generic 100,000-box guard with a direct public-reader regression test.
- Final restore was up to date; solution build passed with 0 warnings and 0 errors.
- Final unit tests passed 84/84; integration tests passed 23/23; solution discovery passed 107/107; all had 0 failed and 0 skipped.
- Explicit H.264/H.265 × progressive/faststart/fragmented Console matrix emitted 3 video and 4 AAC events per file; `ffprobe` identified H.264/HEVC plus AAC and `ffmpeg -v error` produced zero diagnostic bytes for all six files.
- OpenSpec strict validation and both staged/unstaged diff checks passed; remediation tasks are 51/51 complete.

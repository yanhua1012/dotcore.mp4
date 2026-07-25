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

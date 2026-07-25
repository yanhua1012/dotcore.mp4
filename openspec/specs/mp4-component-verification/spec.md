# MP4 Component Verification Specification

## Purpose

Define the project structure, interoperability checks, and executable demonstration required to verify the MP4 component.

## Requirements

### Requirement: Test-first project structure
The solution SHALL contain a .NET Standard 2.0 production library, a separate xUnit unit-test project, a separate xUnit integration-test project, and a .NET 10 Console demonstration project. Tests for a behavior MUST be added before its production implementation task is marked complete.

#### Scenario: Run unit tests independently
- **WHEN** a developer runs the unit-test project
- **THEN** it MUST validate box encoding, codec configuration validation, NAL aggregation/splitting, timestamp conversion, sample-table construction, and error paths without invoking ffmpeg or ffprobe

### Requirement: MP4 interoperability integration verification
The integration-test project SHALL create representative H.264/AAC and H.265/AAC MP4 files through the public writer API, parse them through the public reader API, and compare codec configuration, media payloads, timestamps, durations, and key-frame information. It MUST invoke PATH-resolved `ffprobe` and `ffmpeg` to validate every generated fixture.

#### Scenario: Validate generated MP4 with external tools
- **WHEN** an integration test finalizes a generated MP4 and `ffprobe` and `ffmpeg` are available on PATH
- **THEN** `ffprobe` MUST identify the expected container and codec streams and `ffmpeg` MUST complete decode validation with no error-level diagnostics

#### Scenario: Report missing external verification tools
- **WHEN** ffprobe or ffmpeg is unavailable on PATH
- **THEN** the integration test MUST report a clear skipped or failed prerequisite result naming the missing executable, and MUST NOT claim interoperability verification passed

### Requirement: Console round-trip demonstration
The .NET 10 Console project SHALL demonstrate creating a file-backed stream, configuring and writing video/AAC samples, finalizing an MP4 file, reopening that file, subscribing to video-NAL and AAC events, and printing codec parameters plus every emitted sample's timestamps.

#### Scenario: Run the demonstration
- **WHEN** a developer runs the Console project with an output MP4 path
- **THEN** it MUST write a complete MP4 at that path and print parsed H.264/H.265 parameter sets or AAC parameters together with each video NAL and AAC sample timestamp

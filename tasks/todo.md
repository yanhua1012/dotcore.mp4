# 2026-07-25 MP4 .NET Standard Component Proposal

## Acceptance Criteria

- [x] Create an OpenSpec proposal, design, capability specifications, and implementation checklist for a .NET Standard 2.0 MP4 muxer/demuxer.
- [x] Define test-first xUnit unit/integration coverage, local ffprobe/ffmpeg interoperability verification, and a .NET 10 Console round-trip demo.
- [x] Record stream, codec, timestamp, event, risk, and rollback constraints before implementation.
- [x] Validate that the OpenSpec change is ready to apply.

## Working Notes

- H.265 is included because the requested VPS is an HEVC parameter set; H.264 uses SPS/PPS only.
- Version one writes progressive MP4 to caller-owned seekable streams and fails loudly for non-seekable output; fragmented MP4 is deferred.
- No production code, test project, or media asset is created during this proposal-only change.

## Results

- See `openspec/changes/add-netstandard-mp4-component/` for the implementation-ready proposal artifacts.

# MP4 Stream Muxing Specification

## Purpose

Define the MP4 writer contract for configured H.264/H.265 and AAC ingestion, timing, ownership, and standards-compliant finalization.

## Requirements

### Requirement: .NET Standard MP4 writer contract
The component SHALL expose a .NET Standard 2.0-compatible writer that accepts a caller-owned writable, seekable `Stream` and SHALL not close that stream unless the caller explicitly requests it. The writer MUST reject a null, non-writable, or non-seekable stream before writing MP4 data.

#### Scenario: Reject unsupported output stream
- **WHEN** a caller opens a writer with a stream that cannot seek or cannot write
- **THEN** the writer MUST throw a diagnostic argument or invalid-operation exception and MUST NOT emit a partial MP4 header

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
On successful finalization, the writer SHALL emit a complete ISO Base Media File Format MP4 containing `ftyp`, `mdat`, and `moov` metadata with a video and/or audio track, sample sizes, chunk offsets, decode durations, sync-sample information, and composition offsets when PTS differs from DTS. It MUST reject decreasing DTS within a track.

#### Scenario: Finalize mixed audio and video recording
- **WHEN** configured timed H.264 or H.265 video and configured AAC audio have been written
- **THEN** the resulting stream MUST be a complete MP4 whose track metadata identifies the recorded codecs and whose sample tables describe every accepted sample

#### Scenario: Reject decode-order regression
- **WHEN** a submitted sample DTS is earlier than a preceding sample DTS in the same track
- **THEN** the writer MUST reject that submission with an error identifying the invalid timestamp order

## ADDED Requirements

### Requirement: MP4 stream metadata discovery
The reader SHALL accept a readable, seekable MP4 `Stream`, parse supported H.264/H.265 and AAC tracks, and expose each track's codec configuration before media delivery. It MUST recover H.264 SPS/PPS from `avcC`, H.265 VPS/SPS/PPS from `hvcC`, and AAC AudioSpecificConfig plus its parsed parameters from `esds`.

#### Scenario: Discover codec configuration
- **WHEN** a reader opens an MP4 containing H.265 and AAC tracks created by the writer
- **THEN** the reader MUST expose the original VPS, SPS, PPS, AAC AudioSpecificConfig, sample rate, and channel configuration

### Requirement: Timed video NAL delivery
The reader SHALL parse each supported video sample in decode order, split its length-prefixed sample payload into the original NAL units, and provide PTS, DTS, duration, and key-frame information for every emitted NAL unit. It MUST raise a video-NAL event once per emitted NAL unit during an explicit read operation.

#### Scenario: Deliver all NAL units for an access unit
- **WHEN** a video sample contains multiple H.264 or H.265 NAL units
- **THEN** the reader MUST expose each NAL unit in source order with the same sample timestamps and key-frame state

### Requirement: Timed AAC delivery
The reader SHALL expose each AAC MP4 sample as its original access-unit bytes together with PTS, DTS, duration, and the parsed AAC configuration. It MUST raise an AAC-sample event once per emitted audio sample during an explicit read operation.

#### Scenario: Deliver AAC sample event data
- **WHEN** a caller subscribes to the AAC event and reads a supported MP4 with AAC samples
- **THEN** the event handler MUST receive one callback per AAC sample with its payload, timestamps, duration, sample rate, channel configuration, and AudioSpecificConfig

### Requirement: Reader error and ownership semantics
The reader MUST fail with a diagnostic format exception for unsupported MP4 codecs, malformed length-prefixed NAL data, or inconsistent sample tables, and MUST not silently skip media. It SHALL not close the caller-owned stream unless explicitly requested.

#### Scenario: Reject malformed video sample
- **WHEN** the MP4 declares a video sample whose NAL length exceeds its sample boundary
- **THEN** the reader MUST stop reading and throw a format exception rather than emitting truncated bytes

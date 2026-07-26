## ADDED Requirements

### Requirement: Payload-efficient Reader delivery
Reader SHALL 在維持 constructor-time完整 snapshot 的前提下，讓每個 emitted video NAL或AAC access unit在 caller明確要求 public defensive copy前，至多建立一份 payload-sized owned array。Reader MUST NOT在內部 payload slice已取得 ownership後，再經 public sample constructor建立第二份相同 payload；既有 public constructors、`Data` properties、events、可重複列舉與 Reader dispose後的sample lifetime MUST維持不變。

#### Scenario: Deliver video without duplicate internal ownership copy
- **WHEN** caller在沒有 event subscriber且不讀取 public `Data` property的情況下列舉含大型single-NAL與multi-NAL samples的任一supported layout
- **THEN** Reader MUST為每個emitted NAL建立至多一份payload-sized owned array，且回傳的payload、timestamps、duration與keyframe state MUST與既有行為相同

#### Scenario: Deliver AAC without duplicate internal ownership copy
- **WHEN** caller在沒有 event subscriber且不讀取 public `Data` property的情況下列舉含大型AAC access units的任一supported layout
- **THEN** Reader MUST為每個emitted access unit建立至多一份payload-sized owned array，且回傳的payload、timestamps與duration MUST與既有行為相同

#### Scenario: Preserve defensive-copy isolation
- **WHEN** caller修改public sample `Data`、video event args `Data`或AAC event args `Data`所回傳的array
- **THEN** 修改MUST NOT影響Reader snapshot、其他event/sample instance、後續重複列舉結果或codec configuration

#### Scenario: Preserve delivered sample lifetime
- **WHEN** caller保留已emitted sample、dispose Reader並釋放input stream
- **THEN** sample的public `Data` MUST仍回傳完整payload，且單一小sample MUST NOT僅因internal最佳化而持有完整MP4 snapshot

#### Scenario: Preserve every public delivery entry point
- **WHEN** caller分別使用`ReadVideoNalUnits()`、`EnumerateVideoNalUnits()`、`ReadAudioSamples()`、`EnumerateAacSamples()`、event-only `Read()`或`ReadAll()`
- **THEN** aliases MUST使用相同optimized ownership path，且payload、callback count、跨軌order、timestamps與defensive-copy isolation MUST符合pre-change behavior

#### Scenario: Validate transferred sample invariants
- **WHEN** internal ownership-transfer path收到null/empty payload、negative PTS/DTS或non-positive duration
- **THEN**它MUST和對應public constructor以相同exception type、`ParamName`及語意相等diagnostic拒絕，且只有payload copy步驟MUST不同

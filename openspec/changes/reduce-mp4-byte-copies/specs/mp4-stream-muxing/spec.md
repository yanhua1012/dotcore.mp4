## ADDED Requirements

### Requirement: Payload-efficient video ingestion
Writer SHALL以指向`EncodedVideoNalUnit`已擁有private buffer的validated byte ranges處理raw與Annex-B NAL input，而不得為normalization建立第二份payload-sized NAL arrays。Progressive、faststart與fragmented modes MUST維持NAL order、同PTS/DTS aggregation、length-prefix encoding、timestamp validation、keyframe state與caller mutation isolation。

#### Scenario: Normalize a raw NAL without copying its payload
- **WHEN** caller提交已由`EncodedVideoNalUnit`擁有的大型raw H.264或H.265 NAL
- **THEN** Writer MUST以涵蓋既有private buffer的單一validated range表示該NAL，且MUST NOT為normalization配置另一份NAL-sized array

#### Scenario: Split Annex-B without copying NAL payloads
- **WHEN** caller提交含三或四位元組start codes及多個NAL units的Annex-B sample
- **THEN** Writer MUST以來源順序產生排除start codes的validated ranges，拒絕既有invalid/empty cases，且MUST NOT為每個NAL配置payload array

#### Scenario: Preserve access-unit aggregation bytes
- **WHEN** caller以相同PTS/DTS提交多個contiguous NAL units並完成任一writer mode
- **THEN** output MUST與最佳化前固定fixture byte-identical，且Reader round-trip MUST回傳相同NAL boundaries、timing與keyframe state

#### Scenario: Reject normalization without partial state
- **WHEN** invalid Annex-B boundaries、empty NAL或length overflow使normalization拒絕submitted sample
- **THEN** Writer MUST NOT保留該sample的任何range、改變buffer/timestamp state或寫出該sample，且後續finalization MUST只包含先前已接受samples

### Requirement: Payload-efficient fragmented buffering
Fragmented Writer MUST以contiguous AAC payload或video NAL ranges保存pending fragment data，並以實際length-prefix加payload bytes計入既有fragment buffer limit。它MUST NOT為每個video access unit materialize完整的第二份length-prefixed payload，且成功flush後MUST釋放該fragment對payload backing arrays的references。

#### Scenario: Buffer a GOP without materializing video access units
- **WHEN** fragmented Writer接受由keyframe、multiple-NAL non-keyframes與下一個keyframe界定的GOP
- **THEN** pending fragment MUST保存owned payload ranges與checked encoded sizes，MUST NOT保存每個video sample的第二份完整encoded payload，且retained encoded bytes MUST NOT超過configured limit

#### Scenario: Flush ranged payloads sequentially
- **WHEN**下一個keyframe或finalization觸發fragment flush至writable non-seekable stream
- **THEN** Writer MUST依既有video-first、audio-second payload order寫入相同`moof`/`mdat` bytes，並在成功後移除已提交sample references與精確扣除buffered byte count

#### Scenario: Preserve fragment buffer rejection boundary
- **WHEN**加入下一個NAL range或AAC payload將使length-prefix加payload總數超過`MaximumFragmentBufferBytes`
- **THEN** Writer MUST在寫出不一致fragment前以既有error semantics拒絕operation，MUST NOT保留rejected ranges或推進timestamp state，且MUST NOT因range representation少算或重複計算logical encoded bytes

#### Scenario: Preserve state after fragment output failure
- **WHEN** output Stream在`moof`、`mdat` header或range payload write期間拋出exception
- **THEN** Writer MUST NOT提前移除selected samples、扣除logical buffered bytes或推進fragment sequence state，且MUST維持既有partial-output failure semantics

### Requirement: Single-build fragment metadata
Fragmented Writer SHALL在一次邏輯`moof` build中保留`trun.data_offset` patch positions，並在box length確定後以checked big-endian writes原地解析video與audio offsets。Writer MUST以checked conservative capacity預先配置常態buffer，MUST NOT為相同fragment建立provisional與final兩份完整`moof`，且patch MUST NOT改變box length或其他metadata bytes。

#### Scenario: Resolve mixed-track data offsets in one buffer
- **WHEN** fragment同時包含多個video與AAC samples及composition offsets
- **THEN** Writer MUST只執行一次final `moof` logical build、將video offset指向`mdat`第一個video payload、將audio offset指向video payload之後，且output MUST與既有fixture byte-identical

#### Scenario: Reject an unrepresentable patched offset
- **WHEN** computed `trun.data_offset`無法以required signed 32-bit field表示或patch position不在`moof` boundary內
- **THEN** Writer MUST在提交fragment前拋出可診斷exception，且MUST NOT寫出部分patched metadata

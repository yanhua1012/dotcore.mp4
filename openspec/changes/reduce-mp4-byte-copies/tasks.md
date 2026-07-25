## 1. Baselines and performance harness

- [ ] 1.1 定義固定H.264/H.265、raw/Annex-B、single/multi-NAL、tiny-NAL、AAC與三種layout fixture matrix，列出required acceptance ID、logical payload bytes、sample/NAL count、GOP與bounded Stream call診斷。
- [ ] 1.2 在production修改前加入approved public API與production dependency baseline，並讓比較工具在public signature或`netstandard2.0` dependency drift時失敗。
- [ ] 1.3 在production修改前產生固定Writer outputs，保存SHA-256、top-level box/payload摘要及public Reader round-trip expected values。
- [ ] 1.4 新增獨立.NET 10 `benchmarks/DotCore.Mp4.Benchmarks` project與僅限benchmark的dependency，且不得將package reference傳遞至production library。
- [ ] 1.5 實作可重複使用的fixed fixture setup與counting/pre-sized Streams，將fixture、sample、output-buffer生成及output成長排除於measured operation或獨立回報。
- [ ] 1.6 實作Reader constructor snapshot、no-event delivery、event delivery與explicit `Data` access的獨立benchmark scenarios。
- [ ] 1.7 實作Writer raw/Annex-B、single/multi-NAL progressive/faststart ingestion、fragmented長短GOP flush與faststart finalization的獨立benchmark scenarios。
- [ ] 1.8 實作machine-readable result comparator，以完整scenario identity配對baseline/candidate，檢查35% allocation與10% throughput門檻並在mismatch/failure時非零退出。
- [ ] 1.9 以Release configuration完成至少三次獨立process baseline runs，確認非零operations並記錄commit/dirty state、command、harness version、result path/hash、environment、total managed allocated bytes、GC、throughput、Stream calls與各scenario median。

## 2. Reader ownership-transfer regression slice

- [ ] 2.1 先定義Reader large video/AAC、所有canonical/alias/event-only entry points、event on/off、repeated enumeration、caller mutation、factory invariant、Reader dispose後sample lifetime與snapshot-retention測試案例。
- [ ] 2.2 加入failing structural tests，要求Reader-created sample只接收一份payload-length owned array、internal construction不經public defensive-copy constructor且不保存完整snapshot。
- [ ] 2.3 加入或擴充failing behavior tests，證明sample/event `Data` mutation彼此隔離、所有aliases/`Read()` callback order不變且保留小sample不會持有完整snapshot。
- [ ] 2.4 執行targeted Reader tests並保存因internal ownership-transfer path尚未存在而失敗的red baseline。
- [ ] 2.5 抽出public/internal共用的null、empty、PTS、DTS、duration validation，再加入internal named ownership-transfer factories並保持exception type、`ParamName`、語意相等diagnostic及public surface不變。
- [ ] 2.6 將video NAL `Slice`結果接到internal owned factory，維持malformed length delivery-time exception與timing/keyframe semantics。
- [ ] 2.7 將AAC `Slice`結果接到internal owned factory，維持event、configuration與timing semantics。
- [ ] 2.8 執行targeted Reader tests至green，並執行所有Reader/error/resource tests確認snapshot、limits、exceptions與ownership無回歸。

## 3. Zero-copy internal NAL normalization slice

- [ ] 3.1 先定義raw、三/四位元組Annex-B、multiple/tiny NAL、empty/truncated boundaries、length overflow、parameter-set validation、caller mutation與rejection後state測試案例。
- [ ] 3.2 加入failing structural tests，要求normalization回傳backing array/offset/count ranges且不得建立NAL payload arrays。
- [ ] 3.3 加入failing Writer output tests，要求progressive與faststart的single/multi-NAL fixed outputs和pre-change bytes相同。
- [ ] 3.4 執行targeted normalization/Writer tests並保存range representation尚未存在的red baseline。
- [ ] 3.5 實作不為每個NAL配置object的checked internal readonly NAL range value及raw/Annex-B range parser，保持現有empty/malformed diagnostics。
- [ ] 3.6 調整`ValidateParameterSetNalType`與Writer `SingleParameterSet`兩條codec validation路徑以同步檢查ranges、不保存caller-owned arrays且不重新materialize payload。
- [ ] 3.7 調整pending video access unit與progressive/faststart flush，直接依序寫入NAL length及range。
- [ ] 3.8 執行targeted normalization、codec contract與progressive/faststart tests至green，確認aggregation、timestamps、keyframe與bytes不變。

## 4. Fragment payload-source slice

- [ ] 4.1 先定義fragmented large multi-NAL GOP、mixed AAC、non-seekable output、exact logical buffer boundary、overflow、成功state清除及`moof`/`mdat`/mid-payload throwing Stream測試案例。
- [ ] 4.2 加入failing structural tests，要求video fragment sample保存ranges/logical encoded size而非完整第二份length-prefixed payload，並區分不計入limit的Annex-B start-code physical bytes。
- [ ] 4.3 加入failing behavior tests，要求range-based fragment flush bytes、video-first/audio-second order與buffer accounting和baseline完全相同，且任一output failure不得提前移除samples、扣除bytes或推進sequence。
- [ ] 4.4 執行targeted fragmented Writer tests並保存payload-source representation尚未存在的red baseline。
- [ ] 4.5 實作可表示contiguous AAC與video NAL ranges的internal fragment payload source，所有encoded size計算使用checked arithmetic。
- [ ] 4.6 將fragment buffer capacity、selection與移除邏輯改用encoded size，維持first-keyframe、global DTS與configured limit invariants。
- [ ] 4.7 將fragment `mdat` flush改為直接寫入NAL length/ranges或AAC buffer，僅在完整成功後以test-visible deterministic state清除已提交references/accounting且不materialize完整video access unit。
- [ ] 4.8 執行targeted fragmented Writer tests至green，並重跑non-seekable、overflow、ordering、finalization與resource tests。

## 5. Single-build `moof` slice

- [ ] 5.1 先定義video-only、mixed-track、multiple samples、signed composition offsets、data-offset overflow與invalid patch-position測試案例。
- [ ] 5.2 加入failing structural tests，要求movie-fragment builder只執行一次logical build、以checked conservative capacity預先配置常態buffer並回報所有`trun.data_offset` patch positions。
- [ ] 5.3 加入failing byte-level tests，要求patched video/audio offsets、box length及fixed fragment bytes和pre-change baseline相同。
- [ ] 5.4 執行targeted `moof` tests並保存single-buffer patch path尚未存在的red baseline。
- [ ] 5.5 實作internal buffer segment與可直接注入long offset/position測試的checked big-endian patch helper，無須配置近2 GiB buffer即可拒絕out-of-buffer position及unrepresentable offset。
- [ ] 5.6 調整movie-fragment builder在單次build記錄patch positions，完成後依`moof`/`mdat`/video bytes原地解析offsets。
- [ ] 5.7 移除provisional/final `moof`雙重build，僅將有效buffer segment寫入output且維持failure-before-commit語意。
- [ ] 5.8 執行targeted fragment metadata tests至green，並重跑fragmented Reader round-trip/default/offset/resource suites。

## 6. Performance, compatibility, and final verification

- [ ] 6.1 以相同Release command與環境完成至少三次獨立process candidate benchmark runs，產生含commit/dirty state、scenario identity、result path/hash及各scenario median的machine-readable結果。
- [ ] 6.2 執行result comparator，證明required Reader delivery、progressive/faststart ingestion及fragmented flush的total managed allocated bytes至少降低35%，所有required IDs throughput未退化超過10%，且Stream calls不隨payload byte length成長。
- [ ] 6.3 執行approved API與package comparison，證明public surface、dependency-free `netstandard2.0` target與production package dependencies無變動。
- [ ] 6.4 比較所有fixed output SHA-256及box/payload摘要，並執行public Reader round-trip確認configuration、payload、timing、keyframe與event order相同。
- [ ] 6.5 執行mutation、所有Reader entry points、repeated enumeration、malformed input、rejection-state、throwing Stream、resource guard、stream ownership與idempotent finalization regression suites，確認exception type/timing與語意相等diagnostic。
- [ ] 6.6 執行H.264/H.265三種layout integration matrix、Console demonstrations、`ffprobe`及`ffmpeg -v error` interoperability checks。
- [ ] 6.7 更新README，文件化benchmark目的、Release command、fixture matrix、baseline/candidate比較方式、allocation門檻、scope exclusions與結果位置。
- [ ] 6.8 執行correctness、security/privacy、performance/complexity與scope review，確認無pool lifetime、mutable alias、unbounded retention、payload logging、async或public memory API scope creep。
- [ ] 6.9 依mounted checkout規則完成serialized restore、solution build及solution-level test，確認兩個test projects皆有非零discovered/passed count且無skip。
- [ ] 6.10 執行OpenSpec strict validation、`git diff --check`及artifact/task機器計數，並將commands、observed counts、benchmark medians、風險與rollback記錄至`tasks/todo.md` Results。

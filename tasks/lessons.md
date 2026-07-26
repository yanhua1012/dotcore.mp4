# Lessons

## 2026-07-26 — Writer operation gate must be atomic and boundary-tracked

- 分類：incorrect state-machine semantics。
- Failure mode：`EnterOperation`/`EnterFinalize` 以非原子 check-then-set 取得 Active，兩個同步 caller 可同時進入；pre-cancel `FinalizeFileAsync` 在丟出 cancellation 前已設 Active 卻未恢復 Idle；`Dispose` 不參與 gate；`IsOutputRiskFailure` 只對少數 exception 類型進入 Faulted，跨越 output-risk boundary 後其他 exception 恢復 Idle。
- Detection signal：`catch (Exception) when (_state == Active && IsOutputRiskFailure(error))` 在非 IOException 後落回 Idle path；`Dispose` 無 Active 檢查；gate 為純讀寫 `_state`。
- Prevention rule：operation ownership 用 `Interlocked.CompareExchange`；輸出風險以明確 flag 追蹤，跨越後任何 exception/cancel 終結為 Faulted；finalize pre-cancel 與 sync failure 都恢復 Idle；`Dispose` 拒絕 Active。
- Tripwire：新增 pre-cancel finalize 可重用、Dispose-during-active 拒絕、concurrent sync caller 全拒絕、任意 exception 跨 boundary 進 Faulted 四個測試。

## 2026-07-26 — Cross-commit regression comparison needs schema-compatible harness

- 分類：incorrect assumption about comparability。
- Failure mode：async benchmark 的 identity 欄位（IoMode/StreamKind/Concurrency/DelayTicks）隨 async 實作一起引入，pre-async commit（`a5ec87b`）的 baseline JSON 缺這些欄位，使現行 comparator 的 `HasSameIdentity` 與 provenance hash 重算都失敗；舊 comparator 又強制 baseline/candidate 同 commit，迫使前次以同 commit 比較而無意義。
- Detection signal：以 `a5ec87b` baseline 與現行 candidate 比較時，全部 scenario 回報 parameter mismatch，且 baseline provenance invalid。
- Prevention rule：regression comparator 須允許 baseline/candidate 不同 commit（僅要求 harness/runtime/OS/processor 一致）；欲測「fix 是否退化 sync」應以「修復前 clean commit」為 baseline、「修復後」為 candidate，兩者用同一可比較 harness。
- Tripwire：封存前確認 baseline 與 candidate 的 BenchmarkResult schema 一致、provenance hash 可重算，且 comparator 退出口僅在受控環境判讀。

## 2026-07-26 — Shared devcontainer cannot satisfy a 10% throughput gate

- 分類：environment limitation。
- Failure mode：同一 code 的 3 次 capture ops/s 變異係數達 30–116%，median 比較產生 -53% 等假性 regression；deterministic allocation 卻 0.0% delta。
- Detection signal：baseline 各 run ops/s 差距 4–5 倍，遠超 10% gate，且 allocation 完全相同。
- Prevention rule：10% throughput gate 必須在受控/專用環境取樣；shared environment 僅能以 deterministic metrics（allocation、call counts、fixed-output hash）判讀是否退化。
- Tripwire：先比較 allocation/byte-identical 指標，再以 per-run CV 判斷 timing 是否可信；CV 過高時不得以 throughput gate 封存。

## 2026-07-26 — Checked OpenSpec tasks must be replayed against current evidence

- 分類：missing verification。
- Failure mode：implementation tasks 全數標為完成，但保存的 benchmark baseline/candidate 來自同一 post-implementation commit，provenance validation 失敗，且 required throughput gate 實際為 nonzero exit。
- Detection signal：`tasks.md` 顯示 74/74，然而重跑 `compare --throughput-only` 同時輸出六筆 invalid provenance 與十二筆 throughput regression。
- Prevention rule：封存前不得以 checkbox 或敘述性 Results 代替 acceptance command；每個 MUST gate 必須從保存 artifacts 重播且 exit 0。
- Tripwire：機器比對 baseline/candidate commit、dirty state、result path/hash與 comparator exit code；任何一項不符即保持相關 task 未完成。

## 2026-07-26 — Async benchmark must await the complete measured workflow

- 分類：incorrect assumption about repo behavior。
- Failure mode：harness 將 async API 包在 `GetAwaiter().GetResult()` 與 `Task.WaitAll`，並合成 file diagnostics，雖然 elapsed time涵蓋工作，卻未遵守 async dispatcher、`Task.WhenAll`及實際 completion/call metrics contract。
- Detection signal：source search 找到 measured path 的 blocking waits，file-backed result 固定回報 synchronous completion且 async call/byte metrics為零。
- Prevention rule：async benchmark dispatcher 本身必須回傳 Task，先觀察完整 workflow Task 的 completion state，再於 timed region 內 `await`；metrics只能來自實際 instrumentation。
- Tripwire：封存前對 benchmark source 執行 `rg 'GetAwaiter\\(\\)\\.GetResult|Task\\.WaitAll'`，並以 self-test 拒絕未觀察或全零的 required async metrics。

## 2026-07-26 — ripgrep 的 `-h` 是 help，不是 suppress filename

- 分類：missing verification。
- Failure mode：以 `rg -h '<pattern>' | wc -l` 計算 OpenSpec requirements/scenarios/tasks，誤把 ripgrep help 的 135 行當成 artifact count。
- Detection signal：requirements、scenarios、unchecked tasks、checked tasks 四個不相關計數都異常地同為 135，且與檔案規模明顯不符。
- Prevention rule：ripgrep 機器計數一律使用完整的 `--no-filename`，不得沿用其他 grep 工具的 `-h` 短參數習慣。
- Tripwire：回報計數前同時列出逐檔 `rg -c` 與 `rg --no-filename ... | wc -l` 總數，兩者加總必須一致。

## 2026-07-26 — Rejection-state fixture 必須使用可 round-trip 的時間軸

- 分類：incorrect assumption about repo behavior。
- Failure mode：NAL rejection-state test 在 40 ms duration 的首個 sample 後提交 20 ms DTS，卻期待 Reader round-trip 保留該不連續時間；writer sample table 以 duration 建立連續 decode timeline，使測試把 fixture 問題誤判為 state mutation。
- Detection signal：red baseline 除預期 structural failures 外，多出 `Expected 20 ms, Actual 40 ms`，而 payload 與 rejected-sample 排除 assertions 均通過。
- Prevention rule：測試 rejected submission 不推進 timestamp state時，先讓 accepted samples 的 DTS 等於前一 sample DTS + duration；將 rejected sample 放在更晚 DTS，再回到正確的下一個連續 DTS。
- Tripwire：所有 Writer round-trip state tests 同時 assert submitted DTS continuity、payload集合與 rejected payload 缺席。

## 2026-07-26 — Cross-TFM compiler attributes 不可用 Type identity 比較

- 分類：incorrect assumption about repo behavior。
- Failure mode：`net10.0` test 以 `Type.IsDefined(typeof(IsReadOnlyAttribute))` 檢查 `netstandard2.0` production 的 readonly struct；compiler compatibility attribute 可能來自不同 assembly identity，導致實際 readonly struct 被誤判。
- Detection signal：source 與 binary 都包含 `IsReadOnlyAttribute`，但 reflection 的 exact `Type` identity assertion仍失敗。
- Prevention rule：跨 TFM 驗證 compiler marker attribute 時比較完整 type name；只有同一 target framework/assembly identity 可保證時才比較 `Type` object。
- Tripwire：structural reflection tests 必須先以 `CustomAttributeData.AttributeType.FullName` 驗證 marker，再用獨立 mutation/field checks保護語意。

## 2026-07-26 — Reflection collection tests 必須明確轉成 generic sequence

- 分類：missing verification。
- Failure mode：測試 helper 回傳 non-generic `IList`，直接呼叫 LINQ `Single()` 導致 compiler 無法推斷 generic type argument。
- Detection signal：test project build 在執行任何案例前以 CS0411 失敗。
- Prevention rule：對 reflection/non-generic collection 使用 `Cast<object>()` 後再呼叫 LINQ operators。
- Tripwire：subagent 新增測試後至少先完成 test-project compile；若因串行限制暫緩執行，也必須由主線在合併後先 build 再判讀 red/green。

## 2026-07-25 — 手動回報 checklist 數量錯誤

- 分類：missing verification。
- Failure mode：在 commentary 以心算回報 `tasks.md` 有 48 個項目，實際 checkbox 數為 45。
- Detection signal：以 `rg -c '^- \[ \]' openspec/changes/<change>/tasks.md` 取得的機器計數與敘述不一致。
- Prevention rule：任何向使用者回報的 task、test、scenario 或 artifact 數量，都必須先以 CLI 或 `rg` 機器計數，不得靠目視或心算。
- Tripwire：交付 OpenSpec change 前執行 checkbox、requirement、scenario 與 artifact status 計數，並只引用該次命令輸出。

## 2026-07-25 — 跨多份 spec 的總數不可沿用局部計數

- 分類：missing verification。
- Failure mode：驗證工作紀錄先寫成 15 requirements / 36 scenarios，實際三份 delta specs 加總為 16 / 44。
- Detection signal：`rg -c` 的逐檔結果經 `awk` 加總後與工作紀錄不一致。
- Prevention rule：跨多檔案的 requirement/scenario 總數必須在寫入 scorecard 前，以逐檔機器計數後明確加總。
- Tripwire：執行 `rg -c '^### Requirement:' ... | awk -F: '{s+=$2} END {print s}'` 與對應 scenario 命令，並將輸出直接作為報告來源。

## 2026-07-26 — XML Documentation summary tags must be formatted as multi-line

- 分類：documentation standard。
- Failure mode：將單行 XML documentation summary 標籤（如 `/// <summary>...</summary>`）寫在同一行，不符合專案的多行 XML doc 格式標準。
- Detection signal：`/// <summary> text </summary>` 存在於單一行中。
- Prevention rule：所有 XML doc `<summary>` 標籤，不論內容長短，皆須獨立換行寫成多行格式：
  /// <summary>
  /// 說明內容。
  /// </summary>
- Tripwire：撰寫或修改 XML doc 註解後，用 `rg '/// <summary>.+</summary>'` 檢視確保無單行 summary 殘留。

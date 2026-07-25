# Lessons

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

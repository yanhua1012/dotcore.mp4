# Lessons

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

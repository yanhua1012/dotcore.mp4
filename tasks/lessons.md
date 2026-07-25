# Lessons

## 2026-07-25 — 手動回報 checklist 數量錯誤

- 分類：missing verification。
- Failure mode：在 commentary 以心算回報 `tasks.md` 有 48 個項目，實際 checkbox 數為 45。
- Detection signal：以 `rg -c '^- \[ \]' openspec/changes/<change>/tasks.md` 取得的機器計數與敘述不一致。
- Prevention rule：任何向使用者回報的 task、test、scenario 或 artifact 數量，都必須先以 CLI 或 `rg` 機器計數，不得靠目視或心算。
- Tripwire：交付 OpenSpec change 前執行 checkbox、requirement、scenario 與 artifact status 計數，並只引用該次命令輸出。

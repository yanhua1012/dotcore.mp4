# DotCore.Mp4 Agent 指引

本檔適用於 repository 根目錄及所有子目錄；若子目錄另有更靠近工作檔案的 `AGENTS.md`，以較具體的指引為準。

## 語言與溝通

- 除 identifier、API、codec、CLI、路徑、程式碼與必要 technical terms 外，說明、規格、工作紀錄、文件與 public XML documentation 使用正體中文（zh-TW）。
- 先說明可觀察結果與影響，再提供必要證據；不要以敘述性信心代替 command output。
- 無法執行的驗證須說明原因、未驗證範圍及可重播的精確命令。

## Repository map

| 路徑 | 用途 |
| --- | --- |
| `src/DotCore.Mp4/` | dependency-free `netstandard2.0` MP4 mux/demux production library |
| `tests/DotCore.Mp4.Tests/` | xUnit unit、contract、resource、state 與 README snippet tests |
| `tests/DotCore.Mp4.IntegrationTests/` | public round-trip、Console、`ffprobe`/`ffmpeg` interoperability tests |
| `samples/DotCore.Mp4.Console/` | deterministic Console demo 與 CLI acceptance surface |
| `benchmarks/DotCore.Mp4.Benchmarks/` | .NET 10 Release benchmark、compatibility/fixed-output baseline 與 comparator |
| `openspec/` | `spec-driven` planning artifacts、main specs 與 archives |
| `tasks/` | repo-local checklist、results、lessons 與 test matrix |

開始修改前先讀最接近需求的 tests、public contracts、`README.md`、OpenSpec artifacts，以及 `tasks/lessons.md`。

## 不可破壞的契約

- 除非 OpenSpec change 或使用者明確批准，且有 compatibility、dependency 與 rollback 證據，Production library 保持 `netstandard2.0`、nullable enabled、warnings-as-errors，且不新增 production `PackageReference`、native runtime 或外部工具依賴。
- `BenchmarkDotNet` 只能存在於 benchmark project；`ffprobe`/`ffmpeg` 只用於 integration/acceptance，不是 production dependency。
- 除非 change 明確批准，保留 public signatures、legacy progressive/H.264/sync defaults、stream ownership、defensive-copy、Reader sample lifetime、exception type/timing、timestamp semantics 與 fixed MP4 bytes。
- Console contract 是：

  ```text
  DotCore.Mp4.Console <output-path> [progressive|faststart|fragmented] [h264|h265] [sync|async]
  ```

  只給 output path 時維持 progressive、H.264、sync；未知參數必須 nonzero exit，且不得留下宣稱成功的 output。
- Library async path 使用 `async`/`await` 與 `ConfigureAwait(false)`；不得使用 `Task.Run`、`.Result`、`.Wait()` 或其他 sync-over-async。
- 同一 Writer 的 stateful operation 必須維持 atomic fail-fast overlap、取消、Faulted、partial-output、idempotent finalization 與 `leaveOpen` 契約。
- 修改 MP4 layout、sample、offset、ownership 或 timing 時，以 checked arithmetic、明確 validation 與 deterministic failure 為優先。

## 工作流程

1. 先執行 `git status --short`，辨識既有 modified/untracked files；它們一律視為使用者所有。
2. 非瑣碎工作在 `tasks/todo.md` 建立具 acceptance criteria、verification、risk/rollback 的 checklist，一次只保留一項 in progress。
3. Bugfix 或行為變更優先建立可重現的 failing test；確認 red 是預期缺口，再做最小實作、targeted green、最後 regression。
4. 發現新事實推翻計畫、test failure 或 build error 時停止擴張，保存 command/error/test name，更新計畫後再繼續。
5. 使用 subagent 時優先委派獨立、邊界清楚的唯讀盤點或 review；若明確委派實作，必須分離檔案 ownership。共用 output、同檔編輯與 .NET restore/build/test 一律序列化。
6. 使用 `apply_patch` 做手動文字修改；不要覆寫、清理或格式化與需求無關的檔案。
7. 未經要求不要 commit、push、改寫 history、reset、checkout、clean 或刪除使用者資料。

若使用者糾正需求或本次工作揭露可重複的失誤，更新 `tasks/lessons.md`，記錄 failure mode、detection signal、prevention rule 與 tripwire。

## OpenSpec lifecycle

- 先用 JSON 找 source of truth，不猜 change、artifact 或路徑：

  ```bash
  openspec list --json
  openspec status --change "<name>" --json
  ```

- 新 change 或更新 artifacts 時，遵循 status 的 dependency order，並使用：

  ```bash
  openspec instructions <artifact-id> --change "<name>" --json
  ```

  使用回傳的 `planningHome`、`changeRoot`、`artifactPaths`、`actionContext` 與 `contextFiles`；不要自創固定路徑。
- Apply 前執行：

  ```bash
  openspec instructions apply --change "<name>" --json
  ```

  讀完所有 context files，依 task 做 red → minimal implementation → green → regression；完成且有現行證據後才勾選 checkbox。
- `artifacts: done` 或 `isComplete: true` 只表示規劃 artifacts 完成，不代表 implementation tasks 或 acceptance gates 已完成。
- Verify 時逐 requirement 對應 implementation、逐 scenario 對應 test，重播 tasks 內每個 MUST/acceptance command；checkbox 與歷史 Results 不是完成證據。
- Delta spec sync 是 agent-driven intelligent merge：從 status JSON 的 `artifactPaths.specs.existingOutputPaths` 讀取 delta，依 ADDED/MODIFIED/REMOVED/RENAMED 合併到 `openspec/specs/<capability>/spec.md`，保留未提及內容並確保 idempotent。
- Archive 前檢查 artifacts、checked/unchecked tasks、delta/main sync state 與目標路徑。若有 delta specs，必須向使用者明示「同步後封存」或「不同步直接封存」，不得自行猜選。
- Archive 依 repo skill 的單一路徑執行；不要混用 agent-driven sync/move 與另一套 archive 流程。保留 `.openspec.yaml`，並先確認 `openspec/changes/archive/YYYY-MM-DD-<name>` 不存在。
- 使用 standalone store 時先執行 `openspec store list --json`，後續適用命令帶 `--store <id>`；未指定 store 才使用 nearest local `openspec/`。

OpenSpec 最終驗證：

```bash
openspec validate --all --strict --json --no-interactive
```

Requirement、scenario、task 數量必須機器計數；使用逐檔 `rg -c` 與總數交叉核對：

```bash
rg -c '^### Requirement:' <spec-files...>
rg -c '^#### Scenario:' <spec-files...>
rg --no-filename '^### Requirement:' <spec-files...> | wc -l
rg --no-filename '^#### Scenario:' <spec-files...> | wc -l
rg --no-filename '^- \[[xX]\]' "<tasks-file>" | wc -l
rg --no-filename '^- \[ \]' "<tasks-file>" | wc -l
```

禁止用 `rg -h` suppress filename；在 ripgrep 中 `-h` 是 help。

## .NET 環境與 canonical commands

- 需要 .NET SDK 10；production target 仍是 `netstandard2.0`。Repository 未固定 `global.json`，不得隨意新增 SDK pin。
- 此 checkout 位於 `/mnt/c`。Stale assets 可能引用 Windows NuGet fallback path；先執行：

  ```bash
  dotnet restore DotCore.Mp4.sln /p:RestoreFallbackFolders= /p:RestorePackagesPath=/root/.nuget/packages
  ```

- Restore、build、test 必須序列化，後續仍帶空 `RestoreFallbackFolders`：

  ```bash
  dotnet build DotCore.Mp4.sln --no-restore /p:RestoreFallbackFolders= /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true

  dotnet test tests/DotCore.Mp4.Tests/DotCore.Mp4.Tests.csproj --no-restore /p:RestoreFallbackFolders= --logger "console;verbosity=minimal"

  dotnet test tests/DotCore.Mp4.IntegrationTests/DotCore.Mp4.IntegrationTests.csproj --no-restore /p:RestoreFallbackFolders= --logger "console;verbosity=minimal"

  dotnet test DotCore.Mp4.sln --no-restore /p:RestoreFallbackFolders= /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true --logger "console;verbosity=minimal"
  ```

- 不得只看 exit 0；必須確認 unit/integration 都有 nonzero discovered/passed count、0 failed、0 unexpected skipped，以及 build 0 warnings/0 errors。

## Media、Console 與 benchmark 驗證

- MP4 公開行為受影響時，依 scope 覆蓋 H.264/H.265 × progressive/faststart/fragmented × sync/async，assert payload、configuration、PTS/DTS/duration、keyframe、event order 與 observable Console output。
- 代表性 Console invocation：

  ```bash
  dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-fragmented.mp4 fragmented h265 async
  ```

  行為受影響時依 scope 擴充完整 codec/layout/I/O matrix，不以單一案例代替。
- Interoperability commands：

  ```bash
  ffprobe -v error -show_format -show_streams -of json <file>
  ffmpeg -v error -i <file> -map 0 -f null -
  ```

  `ffprobe` 必須辨識預期 video codec 與 AAC；`ffmpeg -v error` 必須無 diagnostics。
- Public API、dependency 或 MP4 byte contract 受影響時執行：

  ```bash
  dotnet build benchmarks/DotCore.Mp4.Benchmarks/DotCore.Mp4.Benchmarks.csproj -c Release --no-restore /p:RestoreFallbackFolders=
  dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks --no-build -- baseline
  dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks --no-build -- self-test
  ```

- 效能主張必須在相同 Release harness/runtime/OS/processor 的受控或專用 host，以 baseline/candidate 各至少 3 個獨立 process、每 run `--operations 101` 比較。Shared devcontainer 的 timing 只能當噪訊診斷，不得用 fixed-output 或 allocation parity 推論 throughput。
- `artifacts/` 雖然 gitignored，benchmark JSON 仍包含 command、environment 與 absolute path。不要把 token、credential 或敏感路徑放入命令列；分享前檢查內容。

## Definition of Done

- 行為符合可測 acceptance criteria，且未擴張到無關 refactor。
- Targeted tests、完整相關 regression、必要的 Console/media interoperability、benchmark baseline/self-test 均有現行證據。
- Build/test output 有 machine-counted nonzero pass count，沒有 silent skip、warning 或 error。
- OpenSpec artifacts、implementation、tests、README 與 main specs 一致；strict validation 通過。
- 執行並通過：

  ```bash
  git diff --check
  git diff --cached --check
  ```

- `tasks/todo.md` 記錄變更、命令、observed results、風險/rollback 與任何未驗證項目。
- 最終回報明確區分「已驗證」、「未驗證」及「只來自歷史/記憶的資訊」。

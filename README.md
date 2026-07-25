# DotCore.Mp4

這個 repository 提供不依賴 native media runtime 的 .NET Standard 2.0 MP4 元件。它只封裝與解析已編碼的 H.264/H.265 NAL units 和 AAC access units，不負責編碼、解碼、轉碼或播放器功能。

## 支援範圍

- `Mp4Writer` 支援 progressive、faststart 與 fragmented 三種 layout，且都支援單一 H.264 或 H.265 視訊軌及單一 AAC 音訊軌。舊有 `Mp4Writer(Stream, bool)` 維持 progressive 預設。
- H.264 必須提供 SPS/PPS；H.265 必須提供 VPS/SPS/PPS；writer 會分別寫入 `avcC`/`hvcC`。
- Parameter sets 必須各自只含一個正確類型的 NAL unit：H.264 SPS/PPS 為 type 7/8，H.265 VPS/SPS/PPS 為 type 32/33/34；錯誤標示會在寫入前拒絕。
- AAC 必須提供與 sample rate、channel configuration 一致的 AudioSpecificConfig；不會從 ADTS header 猜測設定。
- 同一 PTS/DTS 的連續 video NAL units 會聚合成一個 MP4 sample；reader 會再依原始順序逐 NAL 發出事件。AAC access unit 一個對應一個 MP4 sample。
- writer 時間戳公開為 `TimeSpan`，內建產物的 MP4 track timescale 使用 10,000,000，因此 writer 不接受無法精確換算的時間。reader 遇到 FFmpeg 常見的其他 timescale 時，會以 deterministic nearest-tick 規則還原至 100 ns `TimeSpan` 精度。

## Stream 與例外語意

writer 的 stream 契約依 mode 不同：

| Mode | Stream capability | Top-level layout | 使用時機 |
| --- | --- | --- | --- |
| `Progressive` | `CanWrite` + `CanSeek` | `ftyp`、`mdat`、`moov` | 向後相容的預設完整檔案 |
| `FastStart` | `CanRead` + `CanWrite` + `CanSeek` + `SetLength` | `ftyp`、`moov`、`mdat` | 完成後可由檔頭取得 metadata 的快速起播檔案 |
| `Fragmented` | 僅需 `CanWrite` | initial `ftyp`/`moov`，後接 `moof`/`mdat` | 依 keyframe 持續提交已完成 fragments |

兩端預設都不會關閉 caller-owned stream，可用建構子的 `leaveOpen: false` 明確交由元件關閉。writer 必須呼叫 `FinalizeFile()`（或 `Complete()`/`Finish()`）完成最後 metadata/fragment；成功完成後重複呼叫是 idempotent。

Faststart 在 finalization 時以固定 64 KiB buffer 向後搬移既有 `mdat`，不會把完整 payload 載入 managed memory。只有完成且成功關閉的檔案才保證 `moov` 位於 `mdat` 前；它不是錄影進行中的 live playback 保證。若需要原子交付，請寫入 temporary path，完成後再 rename。

Fragmented mode 在第一個 media sample 時凍結 codec tracks，要求至少有 video configuration，且第一個 video access unit 必須是 keyframe。跨 video/audio 的提交 DTS 必須全域非遞減（同 DTS 合法），下一個 video keyframe 會開始新 fragment。`MaximumFragmentBufferBytes` 預設 16 MiB，長 GOP 超限會明確失敗，不會自動切出 non-keyframe fragment；第一版不支援 audio-only fragmented output。

輸入資料、codec parameter sets、duration 和時間戳會在 API 邊界驗證。減少的 DTS、無法精確轉換的時間、malformed MP4 box、超出 sample 邊界的 NAL length、unsupported codec 或不一致 sample table 會以 `Mp4TimestampException` 或 `Mp4FormatException` 明確失敗；reader 不會靜默跳過媒體。

Reader 會在建構時將完整 MP4 snapshot 至 managed memory，不會 tail-follow 持續成長中的 fMP4。單一輸入上限為 256 MiB，累積最多 1,000,000 samples，各 sample table 最多 1,000,000 entries，每個 container 最多 100,000 boxes，另限制 4,096 fragments、每 fragment 1,024 `traf` 與 4,096 `trun`。MPEG-4 descriptor nesting 最深 32 層且最多走訪 4,096 個 descriptors。超出限制、fragment sequence/timeline 倒退、sample overlap 或 range 不在對應 `mdat` 內，都會在大額配置或 payload delivery 前以 `Mp4FormatException` 拒絕。

基本使用方式：

```csharp
using var output = File.Create("recording.mp4");
using var writer = new Mp4Writer(
    output,
    new Mp4WriterOptions
    {
        Mode = Mp4WriteMode.Fragmented,
        MaximumFragmentBufferBytes = 16 * 1024 * 1024
    });
writer.SetVideoCodecConfiguration(videoConfiguration);
writer.SetAudioCodecConfiguration(aacConfiguration);
writer.WriteVideoNalUnit(new EncodedVideoNalUnit(nal, pts, dts, duration, isKeyFrame));
writer.WriteAudioSample(new EncodedAudioSample(aacBytes, pts, dts, duration));
writer.FinalizeFile();

using var input = File.OpenRead("recording.mp4");
using var reader = new Mp4Reader(input);
reader.VideoNalUnitRead += (_, sample) => Console.WriteLine(sample.PresentationTimestamp);
reader.AacSampleRead += (_, sample) => Console.WriteLine(sample.Data.Length);
reader.Read();
```

## Allocation 與 throughput benchmark

`benchmarks/DotCore.Mp4.Benchmarks` 是獨立的 .NET 10 executable；`BenchmarkDotNet` 僅由這個 project 引用，不會傳遞至 production library。Harness 使用固定 deterministic fixtures 分開量測：

- Reader constructor snapshot、no-event delivery、event delivery，以及 caller 明確讀取 `Data`。
- H.264/H.265 raw/Annex-B、single/multi/tiny NAL 的 progressive/faststart ingestion。
- H.264/H.265 fragmented short/long GOP flush，以及 faststart finalization。
- 每個 scenario 的 logical payload/sample/NAL/GOP identity、managed allocation、GC、throughput、同步 Stream calls、commit/dirty state、command、runtime/environment 與 result path/hash。

Tracked `Baselines/compatibility.json` 鎖定 public API、`netstandard2.0` target 與 production 顯式 package 集合；`Baselines/fixed-outputs.json` 鎖定 H.264/H.265 × 三種 layout 的 SHA-256、top-level box/payload 摘要及 public Reader round-trip。以下命令若 public contract、dependency 或 fixed bytes drift 會非零退出：

```bash
dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks -- baseline
dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks -- self-test
```

正式比較需在相同環境、相同 Release harness 下各執行至少三個獨立 process。Fixture setup與預先配置 output buffer不計入 measured operation；目前 acceptance 使用每 process 101 operations 的 median：

```bash
dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks --no-build -- capture --output artifacts/benchmarks/baseline-run-1.json --operations 101
dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks --no-build -- capture --output artifacts/benchmarks/candidate-run-1.json --operations 101

dotnet run -c Release --project benchmarks/DotCore.Mp4.Benchmarks --no-build -- compare \
  --baseline artifacts/benchmarks/baseline-run-1.json \
  --baseline artifacts/benchmarks/baseline-run-2.json \
  --baseline artifacts/benchmarks/baseline-run-3.json \
  --candidate artifacts/benchmarks/candidate-run-1.json \
  --candidate artifacts/benchmarks/candidate-run-2.json \
  --candidate artifacts/benchmarks/candidate-run-3.json \
  --output artifacts/benchmarks/comparison.json
```

Comparator 要求所有 scenario identity/parameters完全相同。至少 1 MiB 的 Reader delivery、progressive/faststart ingestion與fragment flush，managed allocation median 必須降低至少 35%；所有 required IDs 的 throughput median不得退化超過 10%。`artifacts/` 不進版控，保留完整 JSON 與 SHA-256 作為本機/CI evidence。

這個 benchmark 不代表 async I/O、streaming Reader、public borrowed-memory API 或 faststart layout redesign；這些都不在目前 copy-reduction scope。Stream call count是 bounded-write診斷，不能取代 throughput gate。

## Build、測試與互通性驗證

需要 .NET SDK 10，以及 PATH 中的 `ffprobe` 和 `ffmpeg` 才能執行外部工具驗證。mounted checkout 若帶有錯誤的 Visual Studio fallback path，使用空的 `RestoreFallbackFolders`：

```bash
dotnet restore DotCore.Mp4.sln /p:RestoreFallbackFolders= /p:RestorePackagesPath=/root/.nuget/packages
dotnet build DotCore.Mp4.sln --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true
dotnet test tests/DotCore.Mp4.Tests/DotCore.Mp4.Tests.csproj --no-restore --logger "console;verbosity=minimal"
dotnet test tests/DotCore.Mp4.IntegrationTests/DotCore.Mp4.IntegrationTests.csproj --no-restore --logger "console;verbosity=minimal"
dotnet test DotCore.Mp4.sln --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true --logger "console;verbosity=minimal"
```

Console demo：

```bash
dotnet build samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true
dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-progressive.mp4
dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-faststart.mp4 faststart
dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-fragmented.mp4 fragmented
dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-h265-fragmented.mp4 fragmented h265

ffprobe -v error -show_format -show_streams -of json /tmp/dotcore-fragmented.mp4
ffmpeg -v error -i /tmp/dotcore-fragmented.mp4 -map 0 -f null -
```

只提供 output path 的既有 Console invocation 仍使用 progressive H.264；第二參數可明確指定 `progressive`、`faststart` 或 `fragmented`，第三參數可選 `h264` 或 `h265` 且預設為 `h264`。未知 mode 或 codec 會顯示 usage、以非零 exit code 結束，且不建立被宣稱成功的 output。

unit tests 不呼叫外部工具；integration tests 會建立固定的合法 keyframe→non-keyframe→keyframe H.264/AAC 與 H.265/AAC fixtures，對六種 codec/layout 組合執行 public writer/reader round-trip、`ffprobe` 與 `ffmpeg -v error`，並讓 public reader 反向解析 FFmpeg 產生的 `empty_moov + default_base_moof + frag_keyframe` reference files。工具不存在時測試會明確標示缺少的 executable，而不宣稱 interoperability 已通過。

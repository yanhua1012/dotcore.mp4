# DotCore.Mp4

這個 repository 提供不依賴 native media runtime 的 .NET Standard 2.0 MP4 元件。它只封裝與解析已編碼的 H.264/H.265 NAL units 和 AAC access units，不負責編碼、解碼、轉碼或播放器功能。

## 支援範圍

- `Mp4Writer` 產生 progressive、非 fragmented MP4，支援單一 H.264 或 H.265 視訊軌，以及單一 AAC 音訊軌。
- H.264 必須提供 SPS/PPS；H.265 必須提供 VPS/SPS/PPS；writer 會分別寫入 `avcC`/`hvcC`。
- AAC 必須提供與 sample rate、channel configuration 一致的 AudioSpecificConfig；不會從 ADTS header 猜測設定。
- 同一 PTS/DTS 的連續 video NAL units 會聚合成一個 MP4 sample；reader 會再依原始順序逐 NAL 發出事件。AAC access unit 一個對應一個 MP4 sample。
- 時間戳公開為 `TimeSpan`，MP4 track timescale 使用 10,000,000，因此不需要靜默捨入。

## Stream 與例外語意

writer 的輸出 stream 必須同時 `CanWrite`、`CanSeek`；reader 的輸入 stream 必須同時 `CanRead`、`CanSeek`。兩者預設不會關閉 caller-owned stream，可用建構子的 `leaveOpen: false` 明確交由元件關閉。writer 必須呼叫 `FinalizeFile()`（或 `Complete()`/`Finish()`）才會 backpatch `mdat` 並寫入 `moov`。

輸入資料、codec parameter sets、duration 和時間戳會在 API 邊界驗證。減少的 DTS、無法精確轉換的時間、malformed MP4 box、超出 sample 邊界的 NAL length、unsupported codec 或不一致 sample table 會以 `Mp4TimestampException` 或 `Mp4FormatException` 明確失敗；reader 不會靜默跳過媒體。

基本使用方式：

```csharp
using var output = File.Create("recording.mp4");
using var writer = new Mp4Writer(output);
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

## Build、測試與互通性驗證

需要 .NET SDK 10，以及 PATH 中的 `ffprobe` 和 `ffmpeg` 才能執行外部工具驗證。mounted checkout 若帶有錯誤的 Visual Studio fallback path，使用空的 `RestoreFallbackFolders`：

```bash
dotnet restore DotCore.Mp4.sln /p:RestoreFallbackFolders= /p:RestorePackagesPath=/root/.nuget/packages
dotnet build DotCore.Mp4.sln --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true
dotnet test DotCore.Mp4.sln --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true
```

Console demo：

```bash
dotnet build samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-restore /p:BuildProjectReferences=false /p:DisableFastUpToDateCheck=true
dotnet run --project samples/DotCore.Mp4.Console/DotCore.Mp4.Console.csproj --no-build -- /tmp/dotcore-demo.mp4
ffprobe -v error -show_format -show_streams -of json /tmp/dotcore-demo.mp4
ffmpeg -v error -i /tmp/dotcore-demo.mp4 -map 0 -f null -
```

unit tests 不呼叫外部工具；integration tests 會建立固定的合法 H.264/AAC 與 H.265/AAC fixtures，透過 public writer/reader round-trip，並對每個輸出 fixture 執行 `ffprobe -show_format -show_streams` 與 `ffmpeg -v error -i <file> -map 0 -f null -`。工具不存在時測試會明確標示缺少的 executable，而不宣稱 interoperability 已通過。

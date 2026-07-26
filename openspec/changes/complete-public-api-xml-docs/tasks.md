## 1. 專案規範條文修訂

- [x] 1.1 修訂 `openspec/config.yaml` rules，加入強制的正體中文 XML Doc 與 Visual Studio 標籤規範

## 2. MediaContracts.cs XML Doc 補全

- [x] 2.1 補全 `VideoCodec` enum 及其欄位 (`H264`, `H265`) 的正體中文 XML 註解
- [x] 2.2 補全 `VideoCodecConfiguration` 類別、建構子、靜態工廠方法 (`CreateH264`, `CreateH265`) 與公開屬性 (`Codec`, `Vps`, `Sps`, `Pps`, `NalLengthSize`, `NalUnitLengthSize`, `Width`, `Height`) 的正體中文 XML 註解，包含 `<param>` 與 `<returns>`
- [x] 2.3 補全 `EncodedVideoNalUnit` 類別、公開建構子與所有公開屬性 (`Data`, `PresentationTimestamp`, `DecodeTimestamp`, `Duration`, `IsKeyFrame`, `Pts`, `Dts`) 的正體中文 XML 註解，包含 `<param>`
- [x] 2.4 補全 `EncodedAudioSample` 類別、公開建構子與所有公開屬性 (`Data`, `PresentationTimestamp`, `DecodeTimestamp`, `Duration`, `Pts`, `Dts`) 的正體中文 XML 註解，包含 `<param>`
- [x] 2.5 補全 `VideoNalUnitReadEventArgs` 與 `AacSampleReadEventArgs` 類別、公開建構子與所有事件屬性的正體中文 XML 註解，包含 `<param>`

## 3. Mp4Exceptions.cs XML Doc 補全

- [x] 3.1 補全 `Mp4FormatException` 類別及其多載建構子的正體中文 XML 註解，包含 `<param>`
- [x] 3.2 補全 `Mp4TimestampException` 類別及其多載建構子的正體中文 XML 註解，包含 `<param>`

## 4. Mp4Reader.cs 與 Mp4Writer.cs XML Doc 補全

- [x] 4.1 補全 `Mp4Reader` 類別、同步建構子 `Mp4Reader(Stream, bool)`、公開屬性 (`VideoConfiguration`, `AudioConfiguration`)、公開事件 (`VideoNalUnitRead`, `AacSampleRead`)、公開列舉與讀取方法 (`ReadVideoNalUnits`, `ReadAudioSamples`, `EnumerateVideoNalUnits`, `EnumerateAacSamples`, `ReadAll`, `Dispose`) 的正體中文 XML 註解，包含 `<param>` 與 `<returns>`
- [x] 4.2 補全 `Mp4Writer` 類別、同步建構子 `Mp4Writer(Stream, bool)`、公開屬性 (`VideoConfiguration`, `AudioConfiguration`)、組態與寫入方法 (`ConfigureVideo`, `SetVideoConfiguration`, `SetVideoCodecConfiguration`, `ConfigureAudio`, `SetAudioConfiguration`, `SetAudioCodecConfiguration`, `WriteVideo`, `WriteVideoNalUnit`, `WriteAudio`, `WriteAacSample`, `WriteAudioSample`, `FinalizeFile`, `Complete`, `Finish`, `Dispose`) 的正體中文 XML 註解，包含 `<param>` 與 `<returns>`

## 5. 驗證與迴歸測試

- [x] 5.1 執行 `dotnet build` 確保專案無編譯警告與錯誤
- [x] 5.2 執行完整 `dotnet test` 確保單元測試與整合測試全數通過
- [x] 5.3 執行 `git diff --check` 確保無無關檔案更動或格式問題

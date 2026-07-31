using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4;

/// <summary>
/// 指定錄影恢復結果的可信度層級。
/// </summary>
public enum Mp4RecordingRecoveryTier
{
    /// <summary>
    /// 輸入沒有足夠的可驗證媒體可供恢復。
    /// </summary>
    NoRecoverableMedia = 0,

    /// <summary>
    /// 已由可驗證 journal 精確重建錄影。
    /// </summary>
    Exact = 1,

    /// <summary>
    /// 已由完整 fragmented MP4 結構重建錄影。
    /// </summary>
    Structural = 2,

    /// <summary>
    /// 已以呼叫端明確啟用的受限 video-only heuristic 搶救錄影。
    /// </summary>
    Heuristic = 3
}

/// <summary>
/// 設定錄影恢復的可選行為。
/// </summary>
public sealed class Mp4RecordingRecoveryOptions
{
    /// <summary>
    /// 取得或設定是否允許在缺少可用 journal 時嘗試受限的 video-only heuristic 搶救；預設為 <see langword="false"/>。
    /// </summary>
    public bool EnableHeuristicRecovery { get; set; }
}

/// <summary>
/// 表示錄影恢復作業的結果與診斷資訊。
/// </summary>
public readonly struct Mp4RecordingRecoveryResult
{
    /// <summary>
    /// 使用指定的恢復層級、交付 target path 與診斷 warnings 初始化 <see cref="Mp4RecordingRecoveryResult"/>。
    /// </summary>
    /// <param name="tier">恢復輸出的可信度層級。</param>
    /// <param name="targetPath">已交付的 target path；沒有可恢復媒體時為 <see langword="null"/>。</param>
    /// <param name="warnings">不會影響成功交付的診斷 warnings。</param>
    public Mp4RecordingRecoveryResult(
        Mp4RecordingRecoveryTier tier,
        string? targetPath,
        IReadOnlyList<string>? warnings)
    {
        Tier = tier;
        TargetPath = targetPath;
        Warnings = warnings ?? Array.Empty<string>();
    }

    /// <summary>
    /// 取得恢復輸出的可信度層級。
    /// </summary>
    public Mp4RecordingRecoveryTier Tier { get; }

    /// <summary>
    /// 取得已交付 target 的完整 path；沒有成功交付時為 <see langword="null"/>。
    /// </summary>
    public string? TargetPath { get; }

    /// <summary>
    /// 取得恢復作業產生的診斷 warnings。
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; }
}

/// <summary>
/// 提供 path-based MP4 錄影與 sidecar journal 管理的 facade。
/// </summary>
public sealed class Mp4RecordingWriter : IDisposable
{
    private readonly RecordingPaths _paths;
    private readonly Mp4WriterOptions _options;
    private readonly Guid _captureId;
    private FileStream? _capture;
    private FileStream? _journal;
    private VideoCodecConfiguration? _videoConfiguration;
    private AacCodecConfiguration? _audioConfiguration;
    private long? _lastVideoDts;
    private long? _lastAudioDts;
    private long? _lastGlobalDts;
    private bool _hasVideoSample;
    private int _operationGate;
    private RecordingState _state;
    private bool _disposed;

    /// <summary>
    /// 使用目標 MP4 path 與選用的 writer options 初始化 <see cref="Mp4RecordingWriter"/>。
    /// </summary>
    /// <param name="targetPath">完成且驗證後要交付的 MP4 target path。</param>
    /// <param name="options">錄影輸出 layout 與資源限制；省略時使用 progressive 預設。</param>
    /// <exception cref="ArgumentException">當 <paramref name="targetPath"/> 不是有效本機檔案 path 時擲出。</exception>
    /// <exception cref="IOException">當 target 或其 journal 已存在，或無法獨占建立 artifacts 時擲出。</exception>
    public Mp4RecordingWriter(string targetPath, Mp4WriterOptions? options = null)
        : this(RecordingPaths.Create(targetPath), CloneOptions(options))
    {
        Initialize();
    }

    private Mp4RecordingWriter(RecordingPaths paths, Mp4WriterOptions options)
    {
        _paths = paths;
        _options = options;
        _captureId = Guid.NewGuid();
    }

    /// <summary>
    /// 以非同步方式建立 path-based 錄影 facade。
    /// </summary>
    /// <param name="targetPath">完成且驗證後要交付的 MP4 target path。</param>
    /// <param name="options">錄影輸出 layout 與資源限制；省略時使用 progressive 預設。</param>
    /// <param name="cancellationToken">可取消建立作業的 token。</param>
    /// <returns>可寫入的錄影 facade。</returns>
    public static async Task<Mp4RecordingWriter> CreateAsync(
        string targetPath,
        Mp4WriterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = new Mp4RecordingWriter(RecordingPaths.Create(targetPath), CloneOptions(options));
        try
        {
            await writer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return writer;
        }
        catch
        {
            writer.DisposeFiles();
            throw;
        }
    }

    /// <summary>
    /// 取得目前設定的視訊編解碼器組態；尚未設定時為 <see langword="null"/>。
    /// </summary>
    public VideoCodecConfiguration? VideoConfiguration => _videoConfiguration;

    /// <summary>
    /// 取得完成且通過 strict 驗證後才會交付的完整 target path。
    /// </summary>
    public string TargetPath => _paths.TargetPath;

    /// <summary>
    /// 取得與 target 位於同一資料夾的 sidecar journal 完整 path。
    /// </summary>
    public string JournalPath => _paths.JournalPath;

    /// <summary>
    /// 取得與 target 位於同一資料夾、保存 immutable 媒體 payload 的 capture 完整 path。
    /// </summary>
    public string CapturePath => _paths.CapturePath(_captureId);

    /// <summary>
    /// 取得目前設定的 AAC 編解碼器組態；尚未設定時為 <see langword="null"/>。
    /// </summary>
    public AacCodecConfiguration? AudioConfiguration => _audioConfiguration;

    /// <summary>
    /// 設定錄影使用的視訊編解碼器組態，並將不可變設定寫入 journal。
    /// </summary>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    public void SetVideoCodecConfiguration(VideoCodecConfiguration configuration)
    {
        EnterOperation();
        if (configuration == null)
        {
            ExitOperation();
            throw new ArgumentNullException(nameof(configuration));
        }

        if (_videoConfiguration != null || _lastVideoDts.HasValue)
        {
            ExitOperation();
            throw new InvalidOperationException("The video codec configuration can only be set once before video samples are written.");
        }

        try
        {
            RecordingJournal.AppendVideoConfiguration(Journal, configuration);
            Journal.Flush(true);
            _videoConfiguration = configuration;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 設定錄影使用的視訊編解碼器組態別名。
    /// </summary>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    public void SetVideoConfiguration(VideoCodecConfiguration configuration) => SetVideoCodecConfiguration(configuration);

    /// <summary>
    /// 設定錄影使用的視訊編解碼器組態別名。
    /// </summary>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    public void ConfigureVideo(VideoCodecConfiguration configuration) => SetVideoCodecConfiguration(configuration);

    /// <summary>
    /// 以非同步方式設定視訊編解碼器組態並持久化至 journal。
    /// </summary>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    /// <param name="cancellationToken">可取消 journal 寫入的 token。</param>
    /// <returns>代表設定作業的工作。</returns>
    public Task SetVideoCodecConfigurationAsync(
        VideoCodecConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        EnterOperation();
        if (configuration == null)
        {
            ExitOperation();
            throw new ArgumentNullException(nameof(configuration));
        }

        if (_videoConfiguration != null || _lastVideoDts.HasValue)
        {
            ExitOperation();
            throw new InvalidOperationException("The video codec configuration can only be set once before video samples are written.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SetVideoCodecConfigurationAsyncCore(configuration, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    /// <summary>
    /// 以非同步方式設定視訊編解碼器組態的別名。
    /// </summary>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    /// <param name="cancellationToken">可取消 journal 寫入的 token。</param>
    /// <returns>代表設定作業的工作。</returns>
    public Task SetVideoConfigurationAsync(
        VideoCodecConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SetVideoCodecConfigurationAsync(configuration, cancellationToken);

    /// <summary>
    /// 以非同步方式設定視訊編解碼器組態的別名。
    /// </summary>
    /// <param name="configuration">視訊編解碼器組態資訊。</param>
    /// <param name="cancellationToken">可取消 journal 寫入的 token。</param>
    /// <returns>代表設定作業的工作。</returns>
    public Task ConfigureVideoAsync(
        VideoCodecConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SetVideoCodecConfigurationAsync(configuration, cancellationToken);

    private async Task SetVideoCodecConfigurationAsyncCore(
        VideoCodecConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordingJournal
                .AppendVideoConfigurationAsync(Journal, configuration, cancellationToken)
                .ConfigureAwait(false);
            await Journal.FlushAsync(cancellationToken).ConfigureAwait(false);
            _videoConfiguration = configuration;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 設定錄影使用的 AAC 編解碼器組態，並將不可變設定寫入 journal。
    /// </summary>
    /// <param name="configuration">AAC 編解碼器組態資訊。</param>
    public void SetAudioCodecConfiguration(AacCodecConfiguration configuration)
    {
        EnterOperation();
        if (configuration == null)
        {
            ExitOperation();
            throw new ArgumentNullException(nameof(configuration));
        }

        if (_audioConfiguration != null || _lastAudioDts.HasValue)
        {
            ExitOperation();
            throw new InvalidOperationException("The AAC codec configuration can only be set once before audio samples are written.");
        }

        try
        {
            RecordingJournal.AppendAudioConfiguration(Journal, configuration);
            Journal.Flush(true);
            _audioConfiguration = configuration;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 設定錄影使用的 AAC 編解碼器組態別名。
    /// </summary>
    /// <param name="configuration">AAC 編解碼器組態資訊。</param>
    public void SetAudioConfiguration(AacCodecConfiguration configuration) => SetAudioCodecConfiguration(configuration);

    /// <summary>
    /// 設定錄影使用的 AAC 編解碼器組態別名。
    /// </summary>
    /// <param name="configuration">AAC 編解碼器組態資訊。</param>
    public void ConfigureAudio(AacCodecConfiguration configuration) => SetAudioCodecConfiguration(configuration);

    /// <summary>
    /// 以非同步方式設定 AAC 編解碼器組態並持久化至 journal。
    /// </summary>
    /// <param name="configuration">AAC 編解碼器組態資訊。</param>
    /// <param name="cancellationToken">可取消 journal 寫入的 token。</param>
    /// <returns>代表設定作業的工作。</returns>
    public Task SetAudioCodecConfigurationAsync(
        AacCodecConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        EnterOperation();
        if (configuration == null)
        {
            ExitOperation();
            throw new ArgumentNullException(nameof(configuration));
        }

        if (_audioConfiguration != null || _lastAudioDts.HasValue)
        {
            ExitOperation();
            throw new InvalidOperationException("The AAC codec configuration can only be set once before audio samples are written.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SetAudioCodecConfigurationAsyncCore(configuration, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    /// <summary>
    /// 以非同步方式設定 AAC 編解碼器組態的別名。
    /// </summary>
    /// <param name="configuration">AAC 編解碼器組態資訊。</param>
    /// <param name="cancellationToken">可取消 journal 寫入的 token。</param>
    /// <returns>代表設定作業的工作。</returns>
    public Task SetAudioConfigurationAsync(
        AacCodecConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SetAudioCodecConfigurationAsync(configuration, cancellationToken);

    /// <summary>
    /// 以非同步方式設定 AAC 編解碼器組態的別名。
    /// </summary>
    /// <param name="configuration">AAC 編解碼器組態資訊。</param>
    /// <param name="cancellationToken">可取消 journal 寫入的 token。</param>
    /// <returns>代表設定作業的工作。</returns>
    public Task ConfigureAudioAsync(
        AacCodecConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SetAudioCodecConfigurationAsync(configuration, cancellationToken);

    private async Task SetAudioCodecConfigurationAsyncCore(
        AacCodecConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordingJournal
                .AppendAudioConfigurationAsync(Journal, configuration, cancellationToken)
                .ConfigureAwait(false);
            await Journal.FlushAsync(cancellationToken).ConfigureAwait(false);
            _audioConfiguration = configuration;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 將單一視訊 NAL access unit 寫入 immutable capture，成功後才提交其 journal metadata。
    /// </summary>
    /// <param name="sample">要寫入的視訊 NAL access unit。</param>
    public void WriteVideoNalUnit(EncodedVideoNalUnit sample)
    {
        EnterOperation();
        PreparedSample prepared;
        try
        {
            prepared = PrepareVideoSample(sample);
        }
        catch
        {
            ExitOperation();
            throw;
        }

        try
        {
            WriteSample(prepared);
            _lastVideoDts = prepared.DecodeTimestamp;
            _lastGlobalDts = prepared.DecodeTimestamp;
            _hasVideoSample = true;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 將單一視訊 NAL access unit 寫入錄影的別名。
    /// </summary>
    /// <param name="sample">要寫入的視訊 NAL access unit。</param>
    public void WriteVideo(EncodedVideoNalUnit sample) => WriteVideoNalUnit(sample);

    /// <summary>
    /// 以非同步方式將單一視訊 NAL access unit 寫入 capture 並提交 journal metadata。
    /// </summary>
    /// <param name="sample">要寫入的視訊 NAL access unit。</param>
    /// <param name="cancellationToken">可取消 capture 或 journal 寫入的 token。</param>
    /// <returns>代表寫入作業的工作。</returns>
    public Task WriteVideoNalUnitAsync(
        EncodedVideoNalUnit sample,
        CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            var prepared = PrepareVideoSample(sample);
            cancellationToken.ThrowIfCancellationRequested();
            return WriteVideoNalUnitAsyncCore(prepared, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    /// <summary>
    /// 以非同步方式寫入視訊 NAL access unit 的別名。
    /// </summary>
    /// <param name="sample">要寫入的視訊 NAL access unit。</param>
    /// <param name="cancellationToken">可取消 capture 或 journal 寫入的 token。</param>
    /// <returns>代表寫入作業的工作。</returns>
    public Task WriteVideoAsync(
        EncodedVideoNalUnit sample,
        CancellationToken cancellationToken = default) =>
        WriteVideoNalUnitAsync(sample, cancellationToken);

    private async Task WriteVideoNalUnitAsyncCore(PreparedSample sample, CancellationToken cancellationToken)
    {
        try
        {
            await WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);
            _lastVideoDts = sample.DecodeTimestamp;
            _lastGlobalDts = sample.DecodeTimestamp;
            _hasVideoSample = true;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 將單一 AAC access unit 寫入 immutable capture，成功後才提交其 journal metadata。
    /// </summary>
    /// <param name="sample">要寫入的 AAC access unit。</param>
    public void WriteAudioSample(EncodedAudioSample sample)
    {
        EnterOperation();
        PreparedSample prepared;
        try
        {
            prepared = PrepareAudioSample(sample);
        }
        catch
        {
            ExitOperation();
            throw;
        }

        try
        {
            WriteSample(prepared);
            _lastAudioDts = prepared.DecodeTimestamp;
            _lastGlobalDts = prepared.DecodeTimestamp;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 將單一 AAC access unit 寫入錄影的別名。
    /// </summary>
    /// <param name="sample">要寫入的 AAC access unit。</param>
    public void WriteAudio(EncodedAudioSample sample) => WriteAudioSample(sample);

    /// <summary>
    /// 將單一 AAC access unit 寫入錄影的別名。
    /// </summary>
    /// <param name="sample">要寫入的 AAC access unit。</param>
    public void WriteAacSample(EncodedAudioSample sample) => WriteAudioSample(sample);

    /// <summary>
    /// 以非同步方式將單一 AAC access unit 寫入 capture 並提交 journal metadata。
    /// </summary>
    /// <param name="sample">要寫入的 AAC access unit。</param>
    /// <param name="cancellationToken">可取消 capture 或 journal 寫入的 token。</param>
    /// <returns>代表寫入作業的工作。</returns>
    public Task WriteAudioSampleAsync(
        EncodedAudioSample sample,
        CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            var prepared = PrepareAudioSample(sample);
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAudioSampleAsyncCore(prepared, cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    /// <summary>
    /// 以非同步方式寫入 AAC access unit 的別名。
    /// </summary>
    /// <param name="sample">要寫入的 AAC access unit。</param>
    /// <param name="cancellationToken">可取消 capture 或 journal 寫入的 token。</param>
    /// <returns>代表寫入作業的工作。</returns>
    public Task WriteAudioAsync(
        EncodedAudioSample sample,
        CancellationToken cancellationToken = default) =>
        WriteAudioSampleAsync(sample, cancellationToken);

    /// <summary>
    /// 以非同步方式寫入 AAC access unit 的別名。
    /// </summary>
    /// <param name="sample">要寫入的 AAC access unit。</param>
    /// <param name="cancellationToken">可取消 capture 或 journal 寫入的 token。</param>
    /// <returns>代表寫入作業的工作。</returns>
    public Task WriteAacSampleAsync(
        EncodedAudioSample sample,
        CancellationToken cancellationToken = default) =>
        WriteAudioSampleAsync(sample, cancellationToken);

    private async Task WriteAudioSampleAsyncCore(PreparedSample sample, CancellationToken cancellationToken)
    {
        try
        {
            await WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);
            _lastAudioDts = sample.DecodeTimestamp;
            _lastGlobalDts = sample.DecodeTimestamp;
        }
        catch
        {
            FaultAfterArtifactFailure();
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 將 journal-confirmed capture 重建、strict 驗證並安全交付為 target MP4。
    /// </summary>
    public void FinalizeFile()
    {
        EnterFinalization();
        if (!_lastVideoDts.HasValue && !_lastAudioDts.HasValue)
        {
            ExitOperation();
            throw new InvalidOperationException("At least one video or AAC sample is required before finalization.");
        }

        try
        {
            CloseArtifactsForRecovery();
            var result = Mp4RecordingRecovery.Recover(_paths.TargetPath);
            if (result.Tier != Mp4RecordingRecoveryTier.Exact)
            {
                throw new Mp4FormatException("The recording journal did not contain recoverable media.");
            }

            _state = RecordingState.Finalized;
        }
        catch
        {
            _state = RecordingState.Faulted;
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 以非同步方式重建、strict 驗證並安全交付 target MP4。
    /// </summary>
    /// <param name="cancellationToken">可取消重建與驗證 I/O 的 token。</param>
    /// <returns>代表 finalization 作業的工作。</returns>
    public Task FinalizeFileAsync(CancellationToken cancellationToken = default)
    {
        EnterFinalization();
        if (!_lastVideoDts.HasValue && !_lastAudioDts.HasValue)
        {
            ExitOperation();
            throw new InvalidOperationException("At least one video or AAC sample is required before finalization.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FinalizeFileAsyncCore(cancellationToken);
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private async Task FinalizeFileAsyncCore(CancellationToken cancellationToken)
    {
        try
        {
            await CloseArtifactsForRecoveryAsync(cancellationToken).ConfigureAwait(false);
            var result = await Mp4RecordingRecovery
                .RecoverAsync(_paths.TargetPath, null, cancellationToken)
                .ConfigureAwait(false);
            if (result.Tier != Mp4RecordingRecoveryTier.Exact)
            {
                throw new Mp4FormatException("The recording journal did not contain recoverable media.");
            }

            _state = RecordingState.Finalized;
        }
        catch
        {
            _state = RecordingState.Faulted;
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 完成 MP4 檔案寫入的別名。
    /// </summary>
    public void Complete() => FinalizeFile();

    /// <summary>
    /// 完成 MP4 檔案寫入的別名。
    /// </summary>
    public void Finish() => FinalizeFile();

    /// <summary>
    /// 以非同步方式完成 MP4 檔案寫入的別名。
    /// </summary>
    /// <param name="cancellationToken">可取消重建與驗證 I/O 的 token。</param>
    /// <returns>代表 finalization 作業的工作。</returns>
    public Task CompleteAsync(CancellationToken cancellationToken = default) =>
        FinalizeFileAsync(cancellationToken);

    /// <summary>
    /// 以非同步方式完成 MP4 檔案寫入的別名。
    /// </summary>
    /// <param name="cancellationToken">可取消重建與驗證 I/O 的 token。</param>
    /// <returns>代表 finalization 作業的工作。</returns>
    public Task FinishAsync(CancellationToken cancellationToken = default) =>
        FinalizeFileAsync(cancellationToken);

    /// <summary>
    /// 釋放錄影 facade 使用的資源；未完成的 capture 與 journal 會保留供後續 recovery。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (Volatile.Read(ref _operationGate) != 0)
        {
            throw new InvalidOperationException("A recording writer operation is in progress; await it before disposing the writer.");
        }

        _disposed = true;
        DisposeFiles();
    }

    private void Initialize()
    {
        EnsureTargetDoesNotExist();
        FileStream? journal = null;
        FileStream? capture = null;
        try
        {
            journal = OpenJournal(FileMode.CreateNew);
            capture = OpenCapture(_paths.CapturePath(_captureId), FileMode.CreateNew);
            RecordingJournal.WriteHeader(
                journal,
                new RecordingJournalHeader(_options.Mode, _captureId, _paths.TargetIdentity()));
            journal.Flush(true);
            var header = RecordingCapture.BuildHeader(_captureId);
            capture.Write(header, 0, header.Length);
            capture.Flush(true);
            _journal = journal;
            _capture = capture;
            journal = null;
            capture = null;
        }
        finally
        {
            if (capture != null) capture.Dispose();
            if (journal != null) journal.Dispose();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        EnsureTargetDoesNotExist();
        FileStream? journal = null;
        FileStream? capture = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            journal = OpenJournal(FileMode.CreateNew);
            capture = OpenCapture(_paths.CapturePath(_captureId), FileMode.CreateNew);
            await RecordingJournal
                .WriteHeaderAsync(
                    journal,
                    new RecordingJournalHeader(_options.Mode, _captureId, _paths.TargetIdentity()),
                    cancellationToken)
                .ConfigureAwait(false);
            await journal.FlushAsync(cancellationToken).ConfigureAwait(false);
            var header = RecordingCapture.BuildHeader(_captureId);
            await capture.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await capture.FlushAsync(cancellationToken).ConfigureAwait(false);
            _journal = journal;
            _capture = capture;
            journal = null;
            capture = null;
        }
        finally
        {
            if (capture != null) capture.Dispose();
            if (journal != null) journal.Dispose();
        }
    }

    private PreparedSample PrepareVideoSample(EncodedVideoNalUnit sample)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        if (_videoConfiguration == null)
        {
            throw new InvalidOperationException("Configure a video codec before writing video NAL units.");
        }

        var pts = MediaTime.ToTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale);
        var dts = MediaTime.ToTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale);
        var duration = MediaTime.ToTicks(sample.Duration, MediaTime.DefaultTrackTimescale);
        ValidateSampleTiming(dts, duration, _lastVideoDts, "video");
        ValidateFragmentedGlobalTimestamp(dts, "video");
        if (_options.Mode == Mp4WriteMode.Fragmented && !_hasVideoSample && !sample.IsKeyFrame)
        {
            throw new InvalidOperationException("The first fragmented video access unit must be a keyframe.");
        }

        return new PreparedSample(
            RecordingSampleKind.Video,
            RecordingCapture.EncodeVideo(sample.DataBytes, _videoConfiguration.NalLengthSize),
            pts,
            dts,
            duration,
            sample.IsKeyFrame);
    }

    private PreparedSample PrepareAudioSample(EncodedAudioSample sample)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        if (_audioConfiguration == null)
        {
            throw new InvalidOperationException("Configure AAC before writing audio samples.");
        }

        if (_options.Mode == Mp4WriteMode.Fragmented && _videoConfiguration == null)
        {
            throw new InvalidOperationException("Fragmented MP4 output requires video configuration before AAC media.");
        }

        var pts = MediaTime.ToTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale);
        var dts = MediaTime.ToTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale);
        var duration = MediaTime.ToTicks(sample.Duration, MediaTime.DefaultTrackTimescale);
        ValidateSampleTiming(dts, duration, _lastAudioDts, "audio");
        ValidateFragmentedGlobalTimestamp(dts, "audio");
        return new PreparedSample(
            RecordingSampleKind.Audio,
            sample.DataBytes,
            pts,
            dts,
            duration,
            true);
    }

    private void WriteSample(PreparedSample prepared)
    {
        var offset = Capture.Position;
        Capture.Write(prepared.Data, 0, prepared.Data.Length);
        Capture.Flush(true);
        var boundary = Capture.Position;
        RecordingJournal.AppendSample(
            Journal,
            new RecordingJournalSample(
                prepared.Kind,
                offset,
                prepared.Data.Length,
                prepared.PresentationTimestamp,
                prepared.DecodeTimestamp,
                prepared.Duration,
                prepared.IsKeyFrame,
                boundary));
        Journal.Flush(true);
    }

    private async Task WriteSampleAsync(PreparedSample prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = Capture.Position;
        await Capture.WriteAsync(prepared.Data, 0, prepared.Data.Length, cancellationToken).ConfigureAwait(false);
        await Capture.FlushAsync(cancellationToken).ConfigureAwait(false);
        var boundary = Capture.Position;
        await RecordingJournal
            .AppendSampleAsync(
                Journal,
                new RecordingJournalSample(
                    prepared.Kind,
                    offset,
                    prepared.Data.Length,
                    prepared.PresentationTimestamp,
                    prepared.DecodeTimestamp,
                    prepared.Duration,
                    prepared.IsKeyFrame,
                    boundary),
                cancellationToken)
            .ConfigureAwait(false);
        await Journal.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CloseArtifactsForRecovery()
    {
        Capture.Flush(true);
        Journal.Flush(true);
        DisposeFiles();
    }

    private async Task CloseArtifactsForRecoveryAsync(CancellationToken cancellationToken)
    {
        await Capture.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Journal.FlushAsync(cancellationToken).ConfigureAwait(false);
        DisposeFiles();
    }

    private void EnsureTargetDoesNotExist()
    {
        if (File.Exists(_paths.TargetPath))
        {
            throw new IOException("The recording target already exists and will not be replaced.");
        }
    }

    private FileStream OpenJournal(FileMode mode)
    {
        return new FileStream(
            _paths.JournalPath,
            mode,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
    }

    private static FileStream OpenCapture(string path, FileMode mode)
    {
        return new FileStream(
            path,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
    }

    private static Mp4WriterOptions CloneOptions(Mp4WriterOptions? options)
    {
        var source = options ?? new Mp4WriterOptions();
        if (source.Mode != Mp4WriteMode.Progressive &&
            source.Mode != Mp4WriteMode.FastStart &&
            source.Mode != Mp4WriteMode.Fragmented)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (source.MaximumFragmentBufferBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        return new Mp4WriterOptions
        {
            Mode = source.Mode,
            MaximumFragmentBufferBytes = source.MaximumFragmentBufferBytes
        };
    }

    private static void ValidateSampleTiming(long dts, long duration, long? previousDts, string trackName)
    {
        if (dts < 0) throw new Mp4TimestampException(trackName + " DTS must not be negative.");
        if (duration <= 0) throw new Mp4TimestampException(trackName + " duration must be positive.");
        if (previousDts.HasValue && dts < previousDts.Value)
        {
            throw new Mp4TimestampException(trackName + " DTS must not decrease from one submitted sample to the next.");
        }
    }

    private void ValidateFragmentedGlobalTimestamp(long dts, string trackName)
    {
        if (_options.Mode == Mp4WriteMode.Fragmented &&
            _lastGlobalDts.HasValue &&
            dts < _lastGlobalDts.Value)
        {
            throw new Mp4TimestampException(
                trackName + " DTS must not decrease across fragmented video and audio submissions.");
        }
    }

    private void EnterOperation()
    {
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _operationGate, 1, 0) != 0)
        {
            throw new InvalidOperationException("A recording writer operation is already in progress.");
        }
    }

    private void EnterFinalization()
    {
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _operationGate, 1, 0) != 0)
        {
            throw new InvalidOperationException("A recording writer operation is already in progress.");
        }
    }

    private void ExitOperation()
    {
        Interlocked.Exchange(ref _operationGate, 0);
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Mp4RecordingWriter));
        if (_state == RecordingState.Finalized)
        {
            throw new InvalidOperationException("The recording writer has already been finalized.");
        }

        if (_state == RecordingState.Faulted)
        {
            throw new InvalidOperationException("The recording writer is faulted; its capture and journal were preserved for recovery.");
        }
    }

    private void FaultAfterArtifactFailure()
    {
        _state = RecordingState.Faulted;
    }

    private FileStream Capture => _capture ?? throw new InvalidOperationException("The recording capture is no longer available.");

    private FileStream Journal => _journal ?? throw new InvalidOperationException("The recording journal is no longer available.");

    private void DisposeFiles()
    {
        if (_capture != null)
        {
            _capture.Dispose();
            _capture = null;
        }

        if (_journal != null)
        {
            _journal.Dispose();
            _journal = null;
        }
    }

    private sealed class PreparedSample
    {
        public PreparedSample(
            RecordingSampleKind kind,
            byte[] data,
            long presentationTimestamp,
            long decodeTimestamp,
            long duration,
            bool isKeyFrame)
        {
            Kind = kind;
            Data = data;
            PresentationTimestamp = presentationTimestamp;
            DecodeTimestamp = decodeTimestamp;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
        }

        public RecordingSampleKind Kind { get; }

        public byte[] Data { get; }

        public long PresentationTimestamp { get; }

        public long DecodeTimestamp { get; }

        public long Duration { get; }

        public bool IsKeyFrame { get; }
    }

    private enum RecordingState
    {
        Active,
        Finalized,
        Faulted
    }
}

/// <summary>
/// 提供已中斷 path-based 錄影的恢復作業。
/// </summary>
public static class Mp4RecordingRecovery
{
    /// <summary>
    /// 嘗試恢復指定 target path 對應的錄影與 sidecar journal。
    /// </summary>
    /// <param name="targetPath">要交付或已交付的 MP4 target path。</param>
    /// <param name="options">恢復選項；省略時只允許 exact 或 structural recovery。</param>
    /// <returns>恢復層級、target path 與診斷 warnings。</returns>
    public static Mp4RecordingRecoveryResult Recover(
        string targetPath,
        Mp4RecordingRecoveryOptions? options = null)
    {
        return RecordingRecoveryEngine.Recover(RecordingPaths.Create(targetPath), options ?? new Mp4RecordingRecoveryOptions());
    }

    /// <summary>
    /// 以非同步方式嘗試恢復指定 target path 對應的錄影與 sidecar journal。
    /// </summary>
    /// <param name="targetPath">要交付或已交付的 MP4 target path。</param>
    /// <param name="options">恢復選項；省略時只允許 exact 或 structural recovery。</param>
    /// <param name="cancellationToken">可取消恢復作業的 token。</param>
    /// <returns>恢復層級、target path 與診斷 warnings。</returns>
    public static Task<Mp4RecordingRecoveryResult> RecoverAsync(
        string targetPath,
        Mp4RecordingRecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RecordingRecoveryEngine.RecoverAsync(
            RecordingPaths.Create(targetPath),
            options ?? new Mp4RecordingRecoveryOptions(),
            cancellationToken);
    }
}

internal static class RecordingCapture
{
    public const long PayloadOffset = 68;

    public static byte[] BuildHeader(Guid captureId)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new Mp4Writer(
                stream,
                new Mp4WriterOptions { Mode = Mp4WriteMode.Progressive },
                leaveOpen: true))
            {
            }

            var baseHeader = stream.ToArray();
            var header = new byte[checked(baseHeader.Length + 24)];
            Buffer.BlockCopy(baseHeader, 0, header, 0, 24);
            header[24] = 0;
            header[25] = 0;
            header[26] = 0;
            header[27] = 24;
            header[28] = (byte)'f';
            header[29] = (byte)'r';
            header[30] = (byte)'e';
            header[31] = (byte)'e';
            var captureIdentity = captureId.ToByteArray();
            Buffer.BlockCopy(captureIdentity, 0, header, 32, captureIdentity.Length);
            Buffer.BlockCopy(baseHeader, 24, header, 48, baseHeader.Length - 24);
            return header;
        }
    }

    public static bool HasIdentity(Stream stream, Guid captureId)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanSeek || stream.Length < PayloadOffset) return false;
        var originalPosition = stream.Position;
        try
        {
            stream.Position = 24;
            var box = new byte[24];
            var offset = 0;
            while (offset < box.Length)
            {
                var read = stream.Read(box, offset, box.Length - offset);
                if (read == 0) return false;
                offset += read;
            }

            if (box[0] != 0 || box[1] != 0 || box[2] != 0 || box[3] != 24 ||
                box[4] != (byte)'f' || box[5] != (byte)'r' ||
                box[6] != (byte)'e' || box[7] != (byte)'e')
            {
                return false;
            }

            var expected = captureId.ToByteArray();
            for (var index = 0; index < expected.Length; index++)
            {
                if (box[8 + index] != expected[index]) return false;
            }

            return true;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    public static byte[] EncodeVideo(byte[] data, int lengthSize)
    {
        var nals = NalUnits.Normalize(data);
        using (var stream = new MemoryStream())
        {
            foreach (var nal in nals)
            {
                if (nal.Length <= 0 || nal.Length > MaximumNalLength(lengthSize))
                {
                    throw new Mp4FormatException("A video NAL unit does not fit in the configured MP4 length field.");
                }

                WriteNalLength(stream, nal.Length, lengthSize);
                stream.Write(nal.BackingArray, nal.Offset, nal.Count);
            }

            return stream.ToArray();
        }
    }

    public static byte[] DecodeVideo(byte[] encoded, int lengthSize)
    {
        if (encoded == null) throw new ArgumentNullException(nameof(encoded));
        using (var stream = new MemoryStream())
        {
            var offset = 0;
            var count = 0;
            while (offset < encoded.Length)
            {
                if (encoded.Length - offset < lengthSize)
                {
                    throw new Mp4FormatException("A journal-confirmed video sample has an incomplete NAL length field.");
                }

                var length = ReadNalLength(encoded, offset, lengthSize);
                offset += lengthSize;
                if (length <= 0 || length > encoded.Length - offset)
                {
                    throw new Mp4FormatException("A journal-confirmed video sample has an invalid NAL length.");
                }

                stream.WriteByte(0);
                stream.WriteByte(0);
                stream.WriteByte(0);
                stream.WriteByte(1);
                stream.Write(encoded, offset, length);
                offset += length;
                count++;
            }

            if (count == 0) throw new Mp4FormatException("A journal-confirmed video sample is empty.");
            return stream.ToArray();
        }
    }

    private static long MaximumNalLength(int lengthSize)
    {
        if (lengthSize < 1 || lengthSize > 4) throw new ArgumentOutOfRangeException(nameof(lengthSize));
        return lengthSize == 4 ? uint.MaxValue : (1L << (lengthSize * 8)) - 1;
    }

    private static void WriteNalLength(Stream stream, int length, int lengthSize)
    {
        for (var index = lengthSize - 1; index >= 0; index--)
        {
            stream.WriteByte((byte)(length >> (index * 8)));
        }
    }

    private static int ReadNalLength(byte[] data, int offset, int lengthSize)
    {
        uint value = 0;
        for (var index = 0; index < lengthSize; index++) value = (value << 8) | data[offset + index];
        if (value > int.MaxValue) throw new Mp4FormatException("A NAL length exceeds the managed recovery limit.");
        return (int)value;
    }
}

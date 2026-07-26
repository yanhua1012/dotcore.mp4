namespace DotCore.Mp4;

/// <summary>
/// 指定 MP4 寫入器 (Writer) 產生的檔案配置模式。
/// </summary>
public enum Mp4WriteMode
{
    /// <summary>
    /// 將媒體資料寫在前方，並在完成時將 movie metadata 附加於檔尾。
    /// </summary>
    Progressive = 0,

    /// <summary>
    /// 在完成時將 movie metadata 搬移至媒體資料之前，以利完整檔案快速起播。
    /// </summary>
    FastStart = 1,

    /// <summary>
    /// 依視訊 keyframe 邊界依序寫出 movie fragments。
    /// </summary>
    Fragmented = 2
}

/// <summary>
/// 設定 <see cref="Mp4Writer"/> 的輸出模式與資源限制。
/// </summary>
public sealed class Mp4WriterOptions
{
    /// <summary>
    /// 取得或設定輸出配置；預設為 <see cref="Mp4WriteMode.Progressive"/>。
    /// </summary>
    public Mp4WriteMode Mode { get; set; } = Mp4WriteMode.Progressive;

    /// <summary>
    /// 取得或設定單一未完成 fragment 可暫存的最大位元組數；此值必須大於零。
    /// </summary>
    public int MaximumFragmentBufferBytes { get; set; } = 16 * 1024 * 1024;
}

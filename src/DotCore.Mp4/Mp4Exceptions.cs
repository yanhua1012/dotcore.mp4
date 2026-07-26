using System;
using System.IO;

namespace DotCore.Mp4;

/// <summary>
/// 表示 MP4 資料格式錯誤、不支援或內部不一致時所擲出的例外狀況。
/// </summary>
public class Mp4FormatException : IOException
{
    /// <summary>
    /// 使用指定的錯誤訊息初始化 <see cref="Mp4FormatException"/> 類別的新實例。
    /// </summary>
    /// <param name="message">描述錯誤的訊息。</param>
    public Mp4FormatException(string message) : base(message) { }

    /// <summary>
    /// 使用指定的錯誤訊息與導致此例外狀況的內部例外參考初始化 <see cref="Mp4FormatException"/> 類別的新實例。
    /// </summary>
    /// <param name="message">描述錯誤的訊息。</param>
    /// <param name="innerException">導致目前例外狀況的例外；若未指定內部例外，則為 <see langword="null"/>。</param>
    public Mp4FormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// 表示時間標記順序違規（例如 DTS 倒退）或精確轉換失敗時所擲出的例外狀況。
/// </summary>
public sealed class Mp4TimestampException : Mp4FormatException
{
    /// <summary>
    /// 使用指定的錯誤訊息初始化 <see cref="Mp4TimestampException"/> 類別的新實例。
    /// </summary>
    /// <param name="message">描述錯誤的訊息。</param>
    public Mp4TimestampException(string message) : base(message) { }

    /// <summary>
    /// 使用指定的錯誤訊息與導致此例外狀況的內部例外參考初始化 <see cref="Mp4TimestampException"/> 類別的新實例。
    /// </summary>
    /// <param name="message">描述錯誤的訊息。</param>
    /// <param name="innerException">導致目前例外狀況的例外；若未指定內部例外，則為 <see langword="null"/>。</param>
    public Mp4TimestampException(string message, Exception innerException) : base(message, innerException) { }
}

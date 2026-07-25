using System;
using System.IO;

namespace DotCore.Mp4;

/// <summary>Identifies malformed, unsupported, or internally inconsistent MP4 data.</summary>
public class Mp4FormatException : IOException
{
    public Mp4FormatException(string message) : base(message) { }
    public Mp4FormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Identifies a timestamp order or exact-conversion violation.</summary>
public sealed class Mp4TimestampException : Mp4FormatException
{
    public Mp4TimestampException(string message) : base(message) { }
    public Mp4TimestampException(string message, Exception innerException) : base(message, innerException) { }
}

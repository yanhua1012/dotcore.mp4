using System;
using System.IO;
using System.Linq;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// 媒體元資料表格與 CTS 偏移量 Box 生成測試套件。
/// </summary>
public sealed class MetadataTableTests
{
    [Fact]
    public void FinalizationWritesRequiredBoxesAndCompositionOffsets()
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x65, 0x01 }, TimeSpan.FromMilliseconds(40), TimeSpan.Zero));
            writer.WriteVideoNalUnit(TestMedia.Video(new byte[] { 0x41, 0x02 }, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(40), false));
            writer.WriteAudioSample(TestMedia.Audio(new byte[] { 0x21, 0x10 }, TimeSpan.Zero));
            writer.FinalizeFile();
        }

        var bytes = stream.ToArray();
        foreach (var box in new[] { "ftyp", "mdat", "moov", "avcC", "esds", "stts", "stsc", "stsz", "stco", "stss", "ctts" })
        {
            Assert.True(Contains(bytes, box), "Missing box " + box);
        }

        using var reader = new Mp4Reader(stream);
        var video = reader.ReadVideoNalUnits().ToArray();
        Assert.Equal(2, video.Length);
        Assert.Equal(TimeSpan.FromMilliseconds(40), video[0].PresentationTimestamp);
        Assert.Equal(TimeSpan.Zero, video[0].DecodeTimestamp);
    }

    private static bool Contains(byte[] bytes, string value)
    {
        var needle = Encoding.ASCII.GetBytes(value);
        for (var i = 0; i <= bytes.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++) if (bytes[i + j] != needle[j]) match = false;
            if (match) return true;
        }

        return false;
    }
}

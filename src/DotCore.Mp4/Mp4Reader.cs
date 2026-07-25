using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DotCore.Mp4;

/// <summary>Reads supported MP4 tracks and synchronously emits timed media events.</summary>
public sealed class Mp4Reader : IDisposable
{
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly byte[] _data;
    private readonly ParsedTrack? _videoTrack;
    private readonly ParsedTrack? _audioTrack;
    private bool _disposed;

    public Mp4Reader(Stream input, bool leaveOpen = true)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (!input.CanRead || !input.CanSeek)
        {
            throw new InvalidOperationException("MP4 input requires a readable, seekable stream.");
        }

        _input = input;
        _leaveOpen = leaveOpen;
        _data = Snapshot(input);
        var tracks = ParseTracks(_data);
        foreach (var track in tracks)
        {
            if (track.HandlerType == "vide")
            {
                if (_videoTrack != null) throw new Mp4FormatException("The MP4 contains more than one supported video track.");
                _videoTrack = track;
            }
            else if (track.HandlerType == "soun")
            {
                if (_audioTrack != null) throw new Mp4FormatException("The MP4 contains more than one supported audio track.");
                _audioTrack = track;
            }
        }

        if (_videoTrack == null && _audioTrack == null)
        {
            throw new Mp4FormatException("The MP4 contains no supported H.264, H.265, or AAC track.");
        }

        VideoConfiguration = _videoTrack?.VideoConfiguration;
        AudioConfiguration = _audioTrack?.AudioConfiguration;
    }

    public VideoCodecConfiguration? VideoConfiguration { get; }
    public AacCodecConfiguration? AudioConfiguration { get; }

    public event EventHandler<VideoNalUnitReadEventArgs>? VideoNalUnitRead;
    public event EventHandler<AacSampleReadEventArgs>? AacSampleRead;

    /// <summary>Enumerates video NAL units in sample/decode order and raises one event per NAL.</summary>
    public IEnumerable<EncodedVideoNalUnit> ReadVideoNalUnits()
    {
        EnsureReadable();
        if (_videoTrack == null) yield break;
        foreach (var sample in _videoTrack.Samples)
        {
            foreach (var nal in SplitVideoSample(sample, _videoTrack.VideoConfiguration!.NalLengthSize))
            {
                var value = CreateVideoSample(sample, nal, _videoTrack);
                VideoNalUnitRead?.Invoke(this, new VideoNalUnitReadEventArgs(value, _videoTrack.VideoConfiguration));
                yield return value;
            }
        }
    }

    public IEnumerable<EncodedVideoNalUnit> EnumerateVideoNalUnits() => ReadVideoNalUnits();

    /// <summary>Enumerates AAC access units in sample/decode order and raises one event per sample.</summary>
    public IEnumerable<EncodedAudioSample> ReadAudioSamples()
    {
        EnsureReadable();
        if (_audioTrack == null) yield break;
        foreach (var sample in _audioTrack.Samples)
        {
            var value = CreateAudioSample(sample, _audioTrack);
            AacSampleRead?.Invoke(this, new AacSampleReadEventArgs(value, _audioTrack.AudioConfiguration!));
            yield return value;
        }
    }

    public IEnumerable<EncodedAudioSample> EnumerateAacSamples() => ReadAudioSamples();

    /// <summary>Reads both tracks in decode-time order and raises their corresponding events.</summary>
    public void Read()
    {
        EnsureReadable();
        var videoIndex = 0;
        var audioIndex = 0;
        while ((_videoTrack != null && videoIndex < _videoTrack.Samples.Count) ||
               (_audioTrack != null && audioIndex < _audioTrack.Samples.Count))
        {
            var chooseVideo = _audioTrack == null || audioIndex >= _audioTrack.Samples.Count;
            if (_videoTrack != null && videoIndex < _videoTrack.Samples.Count && !chooseVideo && _audioTrack != null)
            {
                chooseVideo = CompareTime(
                    _videoTrack.Samples[videoIndex].Dts,
                    _videoTrack.Timescale,
                    _audioTrack.Samples[audioIndex].Dts,
                    _audioTrack.Timescale) <= 0;
            }

            if (chooseVideo && _videoTrack != null)
            {
                var sample = _videoTrack.Samples[videoIndex++];
                foreach (var nal in SplitVideoSample(sample, _videoTrack.VideoConfiguration!.NalLengthSize))
                {
                    var value = CreateVideoSample(sample, nal, _videoTrack);
                    VideoNalUnitRead?.Invoke(this, new VideoNalUnitReadEventArgs(value, _videoTrack.VideoConfiguration));
                }
            }
            else if (_audioTrack != null)
            {
                var sample = _audioTrack.Samples[audioIndex++];
                var value = CreateAudioSample(sample, _audioTrack);
                AacSampleRead?.Invoke(this, new AacSampleReadEventArgs(value, _audioTrack.AudioConfiguration!));
            }
        }
    }

    public void ReadAll() => Read();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _input.Dispose();
    }

    private static byte[] Snapshot(Stream input)
    {
        var originalPosition = input.Position;
        try
        {
            if (input.Length > int.MaxValue)
            {
                throw new Mp4FormatException("MP4 files larger than 2 GB are not supported by this managed reader.");
            }

            input.Seek(0, SeekOrigin.Begin);
            var result = new byte[(int)input.Length];
            var offset = 0;
            while (offset < result.Length)
            {
                var read = input.Read(result, offset, result.Length - offset);
                if (read <= 0) throw new Mp4FormatException("The MP4 stream ended before its declared length was read.");
                offset += read;
            }

            return result;
        }
        finally
        {
            input.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static IList<ParsedTrack> ParseTracks(byte[] data)
    {
        var topLevel = ReadBoxes(data, 0, data.Length);
        var moov = FindSingle(topLevel, "moov");
        var tracks = new List<ParsedTrack>();
        foreach (var trak in Children(data, moov, "trak"))
        {
            var parsed = ParseTrack(data, trak);
            if (parsed != null) tracks.Add(parsed);
        }

        return tracks;
    }

    private static ParsedTrack? ParseTrack(byte[] data, Box trak)
    {
        var mdia = FindSingle(Children(data, trak, "mdia"), "mdia");
        var handler = FindSingle(Children(data, mdia, "hdlr"), "hdlr");
        var handlerType = FourCc(data, handler.PayloadStart + 8);
        if (handlerType != "vide" && handlerType != "soun") return null;

        var mdhd = FindSingle(Children(data, mdia, "mdhd"), "mdhd");
        var timescale = ParseTimescale(data, mdhd);
        if (timescale <= 0 || timescale > int.MaxValue)
        {
            throw new Mp4FormatException("The MP4 media timescale is outside the supported range.");
        }

        var minf = FindSingle(Children(data, mdia, "minf"), "minf");
        var stbl = FindSingle(Children(data, minf, "stbl"), "stbl");
        var description = ParseSampleDescription(data, FindSingle(Children(data, stbl, "stsd"), "stsd"), handlerType);
        if (handlerType == "vide" && description.VideoConfiguration == null)
        {
            throw new Mp4FormatException("The video track does not contain a supported avc1 or hvc1 sample entry.");
        }

        if (handlerType == "soun" && description.AudioConfiguration == null)
        {
            throw new Mp4FormatException("The audio track does not contain a supported mp4a/AAC sample entry.");
        }

        var durations = ParseTimeToSample(data, FindSingle(Children(data, stbl, "stts"), "stts"));
        var sizes = ParseSampleSizes(data, FindSingle(Children(data, stbl, "stsz"), "stsz"));
        var chunkOffsetsBox = FindFirst(Children(data, stbl, "stco"), "stco") ?? FindSingle(Children(data, stbl, "co64"), "co64");
        var chunkOffsets = ParseChunkOffsets(data, chunkOffsetsBox);
        var chunkMap = ParseSampleToChunk(data, FindSingle(Children(data, stbl, "stsc"), "stsc"));
        var compositionOffsets = ParseCompositionOffsets(data, FindFirst(Children(data, stbl, "ctts"), "ctts"));
        var syncSamples = ParseSyncSamples(data, FindFirst(Children(data, stbl, "stss"), "stss"));
        var samples = BuildSamples(data, durations, sizes, chunkOffsets, chunkMap, compositionOffsets, syncSamples);

        return new ParsedTrack(data, handlerType, timescale, description.VideoConfiguration, description.AudioConfiguration, samples);
    }

    private static SampleDescription ParseSampleDescription(byte[] data, Box stsd, string handlerType)
    {
        RequirePayload(stsd, 8, "stsd");
        var entryCount = U32(data, stsd.PayloadStart + 4);
        if (entryCount != 1) throw new Mp4FormatException("Exactly one sample description is required for a supported track.");
        var entries = ReadBoxes(data, stsd.PayloadStart + 8, stsd.End);
        if (entries.Count != 1) throw new Mp4FormatException("stsd does not contain exactly one sample entry.");
        var entry = entries[0];
        if (handlerType == "vide")
        {
            if (entry.Type != "avc1" && entry.Type != "hvc1")
            {
                throw new Mp4FormatException("Unsupported video sample entry: " + entry.Type + ".");
            }

            RequirePayload(entry, 78, entry.Type);
            var width = U16(data, entry.PayloadStart + 24);
            var height = U16(data, entry.PayloadStart + 26);
            var childBoxes = ReadBoxes(data, entry.PayloadStart + 78, entry.End);
            var codecBox = FindSingle(childBoxes, entry.Type == "avc1" ? "avcC" : "hvcC");
            var configuration = entry.Type == "avc1"
                ? ParseAvcConfiguration(data, codecBox, width, height)
                : ParseHevcConfiguration(data, codecBox, width, height);
            return new SampleDescription(configuration, null);
        }

        if (entry.Type != "mp4a")
        {
            throw new Mp4FormatException("Unsupported audio sample entry: " + entry.Type + ".");
        }

        RequirePayload(entry, 28, "mp4a");
        var child = ReadBoxes(data, entry.PayloadStart + 28, entry.End);
        var esds = FindSingle(child, "esds");
        var asc = ParseAudioSpecificConfig(data, esds);
        return new SampleDescription(null, AacCodecConfiguration.FromAudioSpecificConfig(asc));
    }

    private static VideoCodecConfiguration ParseAvcConfiguration(byte[] data, Box box, int width, int height)
    {
        RequirePayload(box, 7, "avcC");
        var p = box.PayloadStart;
        var lengthSize = (data[p + 4] & 3) + 1;
        var spsCount = data[p + 5] & 0x1f;
        if (spsCount < 1) throw new Mp4FormatException("avcC does not contain an SPS.");
        var offset = p + 6;
        var sps = ReadLengthPrefixedParameterSet(data, box, ref offset, "SPS");
        for (var additional = 1; additional < spsCount; additional++)
        {
            ReadLengthPrefixedParameterSet(data, box, ref offset, "additional SPS");
        }

        if (offset >= box.End) throw new Mp4FormatException("avcC does not contain a PPS count.");
        var ppsCount = data[offset++];
        if (ppsCount < 1) throw new Mp4FormatException("avcC does not contain a PPS.");
        var pps = ReadLengthPrefixedParameterSet(data, box, ref offset, "PPS");
        return VideoCodecConfiguration.CreateH264(sps, pps, lengthSize, width, height);
    }

    private static VideoCodecConfiguration ParseHevcConfiguration(byte[] data, Box box, int width, int height)
    {
        RequirePayload(box, 23, "hvcC");
        var p = box.PayloadStart;
        var lengthSize = (data[p + 21] & 3) + 1;
        var arrayCount = data[p + 22];
        byte[]? vps = null;
        byte[]? sps = null;
        byte[]? pps = null;
        var offset = p + 23;
        for (var array = 0; array < arrayCount; array++)
        {
            if (offset + 3 > box.End) throw new Mp4FormatException("hvcC array header exceeds its box boundary.");
            var nalType = data[offset++] & 0x3f;
            var count = ReadU16(data, ref offset, box.End);
            for (var item = 0; item < count; item++)
            {
                var nal = ReadLengthPrefixedParameterSet(data, box, ref offset, "HEVC parameter set");
                if (nalType == 32 && vps == null) vps = nal;
                if (nalType == 33 && sps == null) sps = nal;
                if (nalType == 34 && pps == null) pps = nal;
            }
        }

        if (vps == null || sps == null || pps == null)
        {
            throw new Mp4FormatException("hvcC must contain VPS, SPS, and PPS arrays.");
        }

        return VideoCodecConfiguration.CreateH265(vps, sps, pps, lengthSize, width, height);
    }

    private static byte[] ParseAudioSpecificConfig(byte[] data, Box esds)
    {
        RequirePayload(esds, 5, "esds");
        var result = FindDescriptor(data, esds.PayloadStart + 4, esds.End, 0x05);
        if (result == null) throw new Mp4FormatException("esds does not contain DecoderSpecificInfo.");
        return Slice(data, result.Value.Start, result.Value.Length);
    }

    private static DescriptorRange? FindDescriptor(byte[] data, long start, long end, byte wantedTag)
    {
        var cursor = start;
        while (cursor < end)
        {
            if (cursor + 2 > end) throw new Mp4FormatException("An MPEG-4 descriptor header exceeds its parent box.");
            var tag = data[cursor++];
            int length;
            var lengthBytes = ReadDescriptorLength(data, cursor, end, out length);
            cursor += lengthBytes;
            var payloadEnd = cursor + length;
            if (payloadEnd > end) throw new Mp4FormatException("An MPEG-4 descriptor exceeds its parent box.");
            if (tag == wantedTag) return new DescriptorRange(cursor, length);

            long nestedStart = cursor;
            if (tag == 0x03 && length >= 3) nestedStart += 3;
            else if (tag == 0x04 && length >= 13) nestedStart += 13;
            if (nestedStart < payloadEnd)
            {
                var nested = FindDescriptor(data, nestedStart, payloadEnd, wantedTag);
                if (nested.HasValue) return nested;
            }

            cursor = payloadEnd;
        }

        return null;
    }

    private static int ReadDescriptorLength(byte[] data, long position, long end, out int length)
    {
        length = 0;
        for (var i = 0; i < 4; i++)
        {
            if (position + i >= end) throw new Mp4FormatException("An MPEG-4 descriptor has an incomplete length.");
            var value = data[position + i];
            if (length > (int.MaxValue >> 7)) throw new Mp4FormatException("An MPEG-4 descriptor is too large.");
            length = (length << 7) | (value & 0x7f);
            if ((value & 0x80) == 0) return i + 1;
        }

        throw new Mp4FormatException("An MPEG-4 descriptor length uses more than four bytes.");
    }

    private static long ParseTimescale(byte[] data, Box mdhd)
    {
        RequirePayload(mdhd, 20, "mdhd");
        var version = data[mdhd.PayloadStart];
        var offset = version == 1 ? mdhd.PayloadStart + 20 : mdhd.PayloadStart + 12;
        if (version != 0 && version != 1) throw new Mp4FormatException("Unsupported mdhd version.");
        return U32(data, offset);
    }

    private static IList<long> ParseTimeToSample(byte[] data, Box box)
    {
        RequirePayload(box, 8, "stts");
        var count = U32(data, box.PayloadStart + 4);
        var result = new List<long>();
        var offset = box.PayloadStart + 8;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 8 > box.End) throw new Mp4FormatException("stts entry exceeds its box boundary.");
            var sampleCount = U32(data, offset);
            var duration = U32(data, offset + 4);
            if (sampleCount == 0 || result.Count > int.MaxValue - sampleCount) throw new Mp4FormatException("stts contains an invalid sample count.");
            for (uint sample = 0; sample < sampleCount; sample++) result.Add(duration);
            offset += 8;
        }

        return result;
    }

    private static IList<long> ParseSampleSizes(byte[] data, Box box)
    {
        RequirePayload(box, 12, "stsz");
        var constantSize = U32(data, box.PayloadStart + 4);
        var count = U32(data, box.PayloadStart + 8);
        var result = new List<long>();
        var offset = box.PayloadStart + 12;
        if (count > int.MaxValue) throw new Mp4FormatException("stsz contains too many samples.");
        for (uint i = 0; i < count; i++)
        {
            var size = constantSize;
            if (constantSize == 0)
            {
                if (offset + 4 > box.End) throw new Mp4FormatException("stsz entry exceeds its box boundary.");
                size = U32(data, offset);
                offset += 4;
            }

            result.Add(size);
        }

        return result;
    }

    private static IList<long> ParseChunkOffsets(byte[] data, Box box)
    {
        var entrySize = box.Type == "co64" ? 8 : 4;
        RequirePayload(box, 8, box.Type);
        var count = U32(data, box.PayloadStart + 4);
        if (count > int.MaxValue) throw new Mp4FormatException(box.Type + " contains too many chunks.");
        var result = new List<long>();
        var offset = box.PayloadStart + 8;
        for (uint i = 0; i < count; i++)
        {
            if (offset + entrySize > box.End) throw new Mp4FormatException(box.Type + " entry exceeds its box boundary.");
            result.Add(entrySize == 8 ? checked((long)U64(data, offset)) : U32(data, offset));
            offset += entrySize;
        }

        return result;
    }

    private static IList<SampleToChunkEntry> ParseSampleToChunk(byte[] data, Box box)
    {
        RequirePayload(box, 8, "stsc");
        var count = U32(data, box.PayloadStart + 4);
        var result = new List<SampleToChunkEntry>();
        var offset = box.PayloadStart + 8;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 12 > box.End) throw new Mp4FormatException("stsc entry exceeds its box boundary.");
            var firstChunk = U32(data, offset);
            var samplesPerChunk = U32(data, offset + 4);
            var description = U32(data, offset + 8);
            if (firstChunk == 0 || samplesPerChunk == 0 || description != 1) throw new Mp4FormatException("stsc contains an unsupported chunk mapping.");
            result.Add(new SampleToChunkEntry(firstChunk, samplesPerChunk));
            offset += 12;
        }

        return result;
    }

    private static IList<long> ParseCompositionOffsets(byte[] data, Box? box)
    {
        if (box == null) return new List<long>();
        var value = box;
        RequirePayload(value, 8, "ctts");
        var version = data[value.PayloadStart];
        if (version != 0 && version != 1) throw new Mp4FormatException("Unsupported ctts version.");
        var count = U32(data, value.PayloadStart + 4);
        var result = new List<long>();
        var offset = value.PayloadStart + 8;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 8 > value.End) throw new Mp4FormatException("ctts entry exceeds its box boundary.");
            var sampleCount = U32(data, offset);
            var compositionOffset = version == 1 ? I32(data, offset + 4) : U32(data, offset + 4);
            if (sampleCount == 0 || result.Count > int.MaxValue - sampleCount) throw new Mp4FormatException("ctts contains an invalid sample count.");
            for (uint sample = 0; sample < sampleCount; sample++) result.Add(compositionOffset);
            offset += 8;
        }

        return result;
    }

    private static ISet<int>? ParseSyncSamples(byte[] data, Box? box)
    {
        if (box == null) return null;
        var value = box;
        RequirePayload(value, 8, "stss");
        var count = U32(data, value.PayloadStart + 4);
        var result = new HashSet<int>();
        var offset = value.PayloadStart + 8;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 4 > value.End) throw new Mp4FormatException("stss entry exceeds its box boundary.");
            var sampleNumber = U32(data, offset);
            if (sampleNumber == 0 || sampleNumber > int.MaxValue) throw new Mp4FormatException("stss contains an invalid sample number.");
            result.Add((int)sampleNumber);
            offset += 4;
        }

        return result;
    }

    private static IList<ParsedSample> BuildSamples(
        byte[] data,
        IList<long> durations,
        IList<long> sizes,
        IList<long> chunkOffsets,
        IList<SampleToChunkEntry> chunkMap,
        IList<long> compositionOffsets,
        ISet<int>? syncSamples)
    {
        if (durations.Count != sizes.Count) throw new Mp4FormatException("stts and stsz describe different sample counts.");
        if (durations.Count != 0 && compositionOffsets.Count != 0 && compositionOffsets.Count != durations.Count)
        {
            throw new Mp4FormatException("ctts does not describe every sample.");
        }

        if (sizes.Count == 0) return new List<ParsedSample>();
        if (chunkOffsets.Count == 0 || chunkMap.Count == 0) throw new Mp4FormatException("Sample data is present without chunk offset tables.");

        var result = new List<ParsedSample>();
        var sampleIndex = 0;
        for (var chunkIndex = 1; chunkIndex <= chunkOffsets.Count; chunkIndex++)
        {
            var mapping = chunkMap[0];
            for (var mapIndex = 1; mapIndex < chunkMap.Count && chunkMap[mapIndex].FirstChunk <= chunkIndex; mapIndex++) mapping = chunkMap[mapIndex];
            if (mapping.FirstChunk > chunkIndex) throw new Mp4FormatException("stsc does not map the first chunk.");
            var offset = chunkOffsets[chunkIndex - 1];
            for (uint inChunk = 0; inChunk < mapping.SamplesPerChunk; inChunk++)
            {
                if (sampleIndex >= sizes.Count) throw new Mp4FormatException("stsc describes more samples than stsz.");
                var size = sizes[sampleIndex];
                if (offset < 0 || size < 0 || offset > data.LongLength - size)
                {
                    throw new Mp4FormatException("A sample offset or size exceeds the MP4 stream boundary.");
                }

                var dts = sampleIndex == 0 ? 0 : checked(result[sampleIndex - 1].Dts + result[sampleIndex - 1].Duration);
                var ctts = compositionOffsets.Count == 0 ? 0 : compositionOffsets[sampleIndex];
                var key = syncSamples == null || syncSamples.Contains(sampleIndex + 1);
                result.Add(new ParsedSample(offset, size, dts + ctts, dts, durations[sampleIndex], key));
                offset = checked(offset + size);
                sampleIndex++;
            }
        }

        if (sampleIndex != sizes.Count) throw new Mp4FormatException("stsc does not map every sample in stsz.");
        return result;
    }

    private IEnumerable<byte[]> SplitVideoSample(ParsedSample sample, int lengthSize)
    {
        var cursor = sample.Offset;
        var end = checked(sample.Offset + sample.Size);
        while (cursor < end)
        {
            if (cursor + lengthSize > end) throw new Mp4FormatException("A length-prefixed video sample ends inside a NAL length field.");
            var length = ReadNalLength(_data, cursor, lengthSize);
            cursor += lengthSize;
            if (length <= 0 || cursor > end - length) throw new Mp4FormatException("A video NAL length exceeds its sample boundary.");
            yield return Slice(_data, cursor, checked((int)length));
            cursor += length;
        }
    }

    private static EncodedVideoNalUnit CreateVideoSample(ParsedSample sample, byte[] nal, ParsedTrack track)
    {
        return new EncodedVideoNalUnit(
            nal,
            MediaTime.FromTicks(sample.Pts, track.Timescale),
            MediaTime.FromTicks(sample.Dts, track.Timescale),
            MediaTime.FromTicks(sample.Duration, track.Timescale),
            sample.IsKeyFrame);
    }

    private static EncodedAudioSample CreateAudioSample(ParsedSample sample, ParsedTrack track)
    {
        return new EncodedAudioSample(
            Slice(track.Data, sample.Offset, checked((int)sample.Size)),
            MediaTime.FromTicks(sample.Pts, track.Timescale),
            MediaTime.FromTicks(sample.Dts, track.Timescale),
            MediaTime.FromTicks(sample.Duration, track.Timescale));
    }

    private static int CompareTime(long left, int leftScale, long right, int rightScale)
    {
        var leftValue = (decimal)left / leftScale;
        var rightValue = (decimal)right / rightScale;
        return leftValue.CompareTo(rightValue);
    }

    private static IList<Box> Children(byte[] data, Box parent, string requiredType)
    {
        var children = ReadBoxes(data, parent.PayloadStart, parent.End);
        var result = new List<Box>();
        foreach (var child in children) if (child.Type == requiredType) result.Add(child);
        return result;
    }

    private static Box FindSingle(IList<Box> boxes, string type)
    {
        Box? found = null;
        foreach (var box in boxes)
        {
            if (box.Type != type) continue;
            if (found != null) throw new Mp4FormatException("The MP4 contains duplicate " + type + " boxes where one was expected.");
            found = box;
        }

        if (found == null) throw new Mp4FormatException("The MP4 is missing required " + type + " metadata.");
        return found;
    }

    private static Box? FindFirst(IList<Box> boxes, string type)
    {
        foreach (var box in boxes) if (box.Type == type) return box;
        return null;
    }

    private static IList<Box> ReadBoxes(byte[] data, long start, long end)
    {
        if (start < 0 || end < start || end > data.LongLength) throw new Mp4FormatException("An MP4 box range is outside the stream boundary.");
        var result = new List<Box>();
        var cursor = start;
        while (cursor < end)
        {
            if (end - cursor < 8) throw new Mp4FormatException("An MP4 box has an incomplete header.");
            var size32 = U32(data, cursor);
            var type = FourCc(data, cursor + 4);
            long header = 8;
            long size;
            if (size32 == 1)
            {
                if (end - cursor < 16) throw new Mp4FormatException("An extended MP4 box header is incomplete.");
                var extended = U64(data, cursor + 8);
                if (extended > long.MaxValue) throw new Mp4FormatException("An MP4 box is too large.");
                size = (long)extended;
                header = 16;
            }
            else if (size32 == 0)
            {
                size = end - cursor;
            }
            else
            {
                size = size32;
            }

            if (size < header || size > end - cursor) throw new Mp4FormatException("An MP4 box length exceeds its parent boundary.");
            result.Add(new Box(cursor, size, header, type));
            cursor += size;
        }

        return result;
    }

    private static void RequirePayload(Box box, long minimum, string type)
    {
        if (box.Size - box.HeaderSize < minimum) throw new Mp4FormatException(type + " payload is truncated.");
    }

    private static byte[] ReadLengthPrefixedParameterSet(byte[] data, Box box, ref long offset, string name)
    {
        if (offset + 2 > box.End) throw new Mp4FormatException(name + " length exceeds its box boundary.");
        var length = U16(data, offset);
        offset += 2;
        if (length == 0 || offset > box.End - length) throw new Mp4FormatException(name + " exceeds its box boundary.");
        var value = Slice(data, offset, length);
        offset += length;
        return value;
    }

    private static uint ReadU16(byte[] data, ref long offset, long end)
    {
        if (offset + 2 > end) throw new Mp4FormatException("A 16-bit field exceeds its box boundary.");
        var result = U16(data, offset);
        offset += 2;
        return result;
    }

    private static long ReadNalLength(byte[] data, long offset, int lengthSize)
    {
        long result = 0;
        for (var i = 0; i < lengthSize; i++) result = (result << 8) | data[offset + i];
        return result;
    }

    private static byte[] Slice(byte[] data, long start, int length)
    {
        if (start < 0 || length < 0 || start > data.LongLength - length) throw new Mp4FormatException("A byte range exceeds the MP4 stream boundary.");
        var value = new byte[length];
        Buffer.BlockCopy(data, checked((int)start), value, 0, length);
        return value;
    }

    private static byte FourCcByte(byte[] data, long position) => data[checked((int)position)];

    private static string FourCc(byte[] data, long position)
    {
        if (position < 0 || position > data.LongLength - 4) throw new Mp4FormatException("An MP4 box type exceeds the stream boundary.");
        return Encoding.ASCII.GetString(data, checked((int)position), 4);
    }

    private static ushort U16(byte[] data, long position)
    {
        if (position < 0 || position > data.LongLength - 2) throw new Mp4FormatException("A 16-bit MP4 field exceeds the stream boundary.");
        var index = checked((int)position);
        return (ushort)((data[index] << 8) | data[index + 1]);
    }

    private static uint U32(byte[] data, long position)
    {
        if (position < 0 || position > data.LongLength - 4) throw new Mp4FormatException("A 32-bit MP4 field exceeds the stream boundary.");
        var index = checked((int)position);
        return ((uint)data[index] << 24) | ((uint)data[index + 1] << 16) | ((uint)data[index + 2] << 8) | data[index + 3];
    }

    private static long I32(byte[] data, long position) => unchecked((int)U32(data, position));

    private static ulong U64(byte[] data, long position)
    {
        return ((ulong)U32(data, position) << 32) | U32(data, position + 4);
    }

    private void EnsureReadable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Mp4Reader));
    }

    private sealed class Box
    {
        public Box(long start, long size, long headerSize, string type)
        {
            Start = start;
            Size = size;
            HeaderSize = headerSize;
            Type = type;
        }

        public long Start { get; }
        public long Size { get; }
        public long HeaderSize { get; }
        public string Type { get; }
        public long PayloadStart => Start + HeaderSize;
        public long End => Start + Size;
    }

    private sealed class ParsedTrack
    {
        public ParsedTrack(byte[] data, string handlerType, long timescale, VideoCodecConfiguration? videoConfiguration, AacCodecConfiguration? audioConfiguration, IList<ParsedSample> samples)
        {
            Data = data;
            HandlerType = handlerType;
            Timescale = checked((int)timescale);
            VideoConfiguration = videoConfiguration;
            AudioConfiguration = audioConfiguration;
            Samples = samples;
        }

        public string HandlerType { get; }
        public int Timescale { get; }
        public VideoCodecConfiguration? VideoConfiguration { get; }
        public AacCodecConfiguration? AudioConfiguration { get; }
        public IList<ParsedSample> Samples { get; }
        public byte[] Data { get; }
    }

    private sealed class SampleDescription
    {
        public SampleDescription(VideoCodecConfiguration? videoConfiguration, AacCodecConfiguration? audioConfiguration)
        {
            VideoConfiguration = videoConfiguration;
            AudioConfiguration = audioConfiguration;
        }

        public VideoCodecConfiguration? VideoConfiguration { get; }
        public AacCodecConfiguration? AudioConfiguration { get; }
    }

    private sealed class ParsedSample
    {
        public ParsedSample(long offset, long size, long pts, long dts, long duration, bool isKeyFrame)
        {
            Offset = offset;
            Size = size;
            Pts = pts;
            Dts = dts;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
        }

        public long Offset { get; }
        public long Size { get; }
        public long Pts { get; }
        public long Dts { get; }
        public long Duration { get; }
        public bool IsKeyFrame { get; }
    }

    private sealed class SampleToChunkEntry
    {
        public SampleToChunkEntry(uint firstChunk, uint samplesPerChunk)
        {
            FirstChunk = checked((int)firstChunk);
            SamplesPerChunk = samplesPerChunk;
        }

        public int FirstChunk { get; }
        public uint SamplesPerChunk { get; }
    }

    private readonly struct DescriptorRange
    {
        public DescriptorRange(long start, int length) { Start = start; Length = length; }
        public long Start { get; }
        public int Length { get; }
    }
}

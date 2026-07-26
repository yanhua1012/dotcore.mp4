using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4;

/// <summary>Reads supported MP4 tracks and synchronously emits timed media events.</summary>
public sealed class Mp4Reader : IDisposable
{
    internal const long MaximumInputBytes = 256L * 1024 * 1024;
    internal const int MaximumSampleCount = 1_000_000;
    internal const int MaximumDescriptorDepth = 32;
    internal const int MaximumFragmentCount = 4_096;
    internal const int MaximumTrackFragmentsPerFragment = 1_024;
    internal const int MaximumTrackRunsPerFragment = 4_096;
    private const int MaximumTableEntryCount = 1_000_000;
    private const int MaximumDescriptorCount = 4_096;
    internal const int MaximumBoxesPerContainer = 100_000;

    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly byte[] _data;
    private ParsedTrack? _videoTrack;
    private ParsedTrack? _audioTrack;
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
        InitializeFromSnapshot();
    }

    /// <summary>以非同步 snapshot 建立 <see cref="Mp4Reader"/>，使用 caller stream 的可取消 <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/> 讀取完整 MP4，再交由與同步 constructor 相同的同步 parser 解析。</summary>
    /// <param name="input">可讀、可搜尋的 caller-owned 輸入 stream；factory 在成功前不會因 <paramref name="leaveOpen"/> 為 <c>false</c> 而關閉它。</param>
    /// <param name="leaveOpen">reader 釋放時是否保持 <paramref name="input"/> 開啟。</param>
    /// <param name="cancellationToken">可取消 snapshot 讀取的 token。</param>
    /// <returns>與同步 constructor 具有相同 configuration、payload、timing 與 events 的 <see cref="Mp4Reader"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 為 <c>null</c>。</exception>
    /// <exception cref="InvalidOperationException"><paramref name="input"/> 不可讀或不可搜尋。</exception>
    /// <exception cref="Mp4FormatException">輸入超過 256 MiB 上限、提前結束或不含受支援的 track。</exception>
    /// <remarks>
    /// factory 在 <c>finally</c> 嘗試恢復 <paramref name="input"/> 的原始 position；restore 失敗時依既有同步 <c>finally</c> 語意由 restore exception 取代先前 read/cancellation failure。Snapshot 完成後的 parsing、events 與列舉仍維持同步 memory-only 行為，不使用 <c>Task.Run</c>。
    /// </remarks>
    public static async Task<Mp4Reader> CreateAsync(
        Stream input,
        bool leaveOpen = true,
        CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (!input.CanRead || !input.CanSeek)
        {
            throw new InvalidOperationException("MP4 input requires a readable, seekable stream.");
        }

        var originalPosition = input.Position;
        byte[] data;
        try
        {
            var length = input.Length;
            if (length < 0 || length > MaximumInputBytes)
            {
                throw new Mp4FormatException(
                    "The MP4 input length exceeds the managed reader limit of " +
                    MaximumInputBytes + " bytes.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            input.Seek(0, SeekOrigin.Begin);
            data = new byte[(int)length];
            var offset = 0;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input
                    .ReadAsync(data, offset, data.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0) throw new Mp4FormatException("The MP4 stream ended before its declared length was read.");
                offset += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            input.Seek(originalPosition, SeekOrigin.Begin);
        }

        var reader = new Mp4Reader(input, leaveOpen, data);
        return reader;
    }

    private Mp4Reader(Stream input, bool leaveOpen, byte[] snapshot)
    {
        _input = input;
        _leaveOpen = leaveOpen;
        _data = snapshot;
        InitializeFromSnapshot();
    }

    private void InitializeFromSnapshot()
    {
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

    public VideoCodecConfiguration? VideoConfiguration { get; private set; }
    public AacCodecConfiguration? AudioConfiguration { get; private set; }

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
            var length = input.Length;
            if (length < 0 || length > MaximumInputBytes)
            {
                throw new Mp4FormatException(
                    "The MP4 input length exceeds the managed reader limit of " +
                    MaximumInputBytes + " bytes.");
            }

            input.Seek(0, SeekOrigin.Begin);
            var result = new byte[(int)length];
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
        var topLevel = ReadBoxes(
            data,
            0,
            data.Length,
            "moof",
            MaximumFragmentCount,
            "MP4 fragment count exceeds the supported limit of " + MaximumFragmentCount + ".");
        var moov = FindSingle(topLevel, "moov");
        var trackBoxes = Children(data, moov, "trak");
        ValidateSupportedTrackMultiplicity(data, trackBoxes);
        var fragments = new List<Box>();
        foreach (var box in topLevel) if (box.Type == "moof") fragments.Add(box);
        if (fragments.Count != 0)
        {
            RejectAmbiguousProgressiveSampleTables(data, trackBoxes);
        }

        var tracks = new List<ParsedTrack>();
        foreach (var trak in trackBoxes)
        {
            var parsed = ParseTrack(data, trak);
            if (parsed != null) tracks.Add(parsed);
        }

        if (fragments.Count == 0) return tracks;
        foreach (var track in tracks)
        {
            if (track.Samples.Count != 0)
            {
                throw new Mp4FormatException(
                    "A supported track ambiguously contains both progressive samples and movie fragments.");
            }
        }

        ParseMovieFragments(data, topLevel, moov, tracks);
        return tracks;
    }

    private static void RejectAmbiguousProgressiveSampleTables(byte[] data, IList<Box> trackBoxes)
    {
        foreach (var trak in trackBoxes)
        {
            var mdia = FindSingle(Children(data, trak, "mdia"), "mdia");
            var handler = FindSingle(Children(data, mdia, "hdlr"), "hdlr");
            var handlerType = ParseHandlerType(data, handler);
            if (handlerType != "vide" && handlerType != "soun") continue;
            var minf = FindSingle(Children(data, mdia, "minf"), "minf");
            var stbl = FindSingle(Children(data, minf, "stbl"), "stbl");
            var stsz = FindSingle(Children(data, stbl, "stsz"), "stsz");
            RequirePayload(stsz, 12, "stsz");
            if (U32(data, stsz.PayloadStart + 8) != 0)
            {
                throw new Mp4FormatException(
                    "A supported track ambiguously contains both progressive samples and movie fragments.");
            }
        }
    }

    private static void ValidateSupportedTrackMultiplicity(byte[] data, IList<Box> tracks)
    {
        var hasVideo = false;
        var hasAudio = false;
        foreach (var trak in tracks)
        {
            var mdia = FindSingle(Children(data, trak, "mdia"), "mdia");
            var handler = FindSingle(Children(data, mdia, "hdlr"), "hdlr");
            var handlerType = ParseHandlerType(data, handler);
            if (handlerType == "vide")
            {
                if (hasVideo) throw new Mp4FormatException("The MP4 contains more than one supported video track.");
                hasVideo = true;
            }
            else if (handlerType == "soun")
            {
                if (hasAudio) throw new Mp4FormatException("The MP4 contains more than one supported audio track.");
                hasAudio = true;
            }
        }
    }

    private static ParsedTrack? ParseTrack(byte[] data, Box trak)
    {
        var trackHeader = FindSingle(Children(data, trak, "tkhd"), "tkhd");
        var trackId = ParseTrackId(data, trackHeader);
        var mdia = FindSingle(Children(data, trak, "mdia"), "mdia");
        var handler = FindSingle(Children(data, mdia, "hdlr"), "hdlr");
        var handlerType = ParseHandlerType(data, handler);
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

        var sizes = ParseSampleSizes(data, FindSingle(Children(data, stbl, "stsz"), "stsz"));
        var durations = ParseTimeToSample(
            data,
            FindSingle(Children(data, stbl, "stts"), "stts"),
            sizes.Count);
        var chunkOffsetsBox = FindFirst(Children(data, stbl, "stco"), "stco") ?? FindSingle(Children(data, stbl, "co64"), "co64");
        var chunkOffsets = ParseChunkOffsets(data, chunkOffsetsBox);
        var chunkMap = ParseSampleToChunk(data, FindSingle(Children(data, stbl, "stsc"), "stsc"));
        var compositionOffsets = ParseCompositionOffsets(
            data,
            FindFirst(Children(data, stbl, "ctts"), "ctts"),
            sizes.Count);
        var syncSamples = ParseSyncSamples(
            data,
            FindFirst(Children(data, stbl, "stss"), "stss"),
            sizes.Count);
        var samples = BuildSamples(data, durations, sizes, chunkOffsets, chunkMap, compositionOffsets, syncSamples);

        return new ParsedTrack(data, trackId, handlerType, timescale, description.VideoConfiguration, description.AudioConfiguration, samples);
    }

    private static uint ParseTrackId(byte[] data, Box tkhd)
    {
        RequirePayload(tkhd, 4, "tkhd");
        var version = data[tkhd.PayloadStart];
        long offset;
        if (version == 0)
        {
            RequirePayload(tkhd, 16, "tkhd");
            offset = tkhd.PayloadStart + 12;
        }
        else if (version == 1)
        {
            RequirePayload(tkhd, 24, "tkhd");
            offset = tkhd.PayloadStart + 20;
        }
        else throw new Mp4FormatException("Unsupported tkhd version.");
        var trackId = U32(data, offset);
        if (trackId == 0) throw new Mp4FormatException("A supported track has an invalid zero track ID.");
        return trackId;
    }

    private static string ParseHandlerType(byte[] data, Box handler)
    {
        RequirePayload(handler, 12, "hdlr");
        return FourCc(data, handler.PayloadStart + 8);
    }

    private static void ParseMovieFragments(
        byte[] data,
        IList<Box> topLevel,
        Box moov,
        IList<ParsedTrack> tracks)
    {
        var trackById = new Dictionary<uint, ParsedTrack>();
        foreach (var track in tracks)
        {
            if (trackById.ContainsKey(track.TrackId))
            {
                throw new Mp4FormatException("Supported tracks contain a duplicate track ID.");
            }

            trackById.Add(track.TrackId, track);
        }

        var defaults = ParseTrackExtendsDefaults(data, moov, trackById);
        var decodeEnds = new Dictionary<uint, long>();
        uint? previousSequence = null;
        var totalSamples = 0;
        for (var topIndex = 0; topIndex < topLevel.Count; topIndex++)
        {
            var moof = topLevel[topIndex];
            if (moof.Type != "moof") continue;
            if (topIndex + 1 >= topLevel.Count || topLevel[topIndex + 1].Type != "mdat")
            {
                throw new Mp4FormatException("Each movie fragment must be followed by an mdat box.");
            }

            var mdat = topLevel[topIndex + 1];
            try
            {
                var moofChildren = ReadBoxes(
                    data,
                    moof.PayloadStart,
                    moof.End,
                    "traf",
                    MaximumTrackFragmentsPerFragment,
                    "A movie fragment traf count exceeds the supported limit of " +
                    MaximumTrackFragmentsPerFragment + ".");
                var sequence = ParseFragmentSequence(data, moofChildren);
                if (previousSequence.HasValue && sequence <= previousSequence.Value)
                {
                    throw new Mp4FormatException("Movie fragment sequence numbers must be strictly increasing.");
                }

                previousSequence = sequence;
                ParseMovieFragment(
                    data,
                    moof,
                    mdat,
                    moofChildren,
                    trackById,
                    defaults,
                    decodeEnds,
                    ref totalSamples);
            }
            catch (OverflowException error)
            {
                throw new Mp4FormatException("Movie fragment timing or data offsets exceed the supported range.", error);
            }
        }
    }

    private static Dictionary<uint, FragmentDefaults> ParseTrackExtendsDefaults(
        byte[] data,
        Box moov,
        IDictionary<uint, ParsedTrack> trackById)
    {
        var mvex = FindSingle(Children(data, moov, "mvex"), "mvex");
        var result = new Dictionary<uint, FragmentDefaults>();
        foreach (var trex in Children(data, mvex, "trex"))
        {
            RequirePayload(trex, 24, "trex");
            var trackId = U32(data, trex.PayloadStart + 4);
            if (!trackById.ContainsKey(trackId)) continue;
            if (result.ContainsKey(trackId))
            {
                throw new Mp4FormatException("The initial movie contains duplicate trex defaults for a supported track.");
            }

            var descriptionIndex = U32(data, trex.PayloadStart + 8);
            if (descriptionIndex != 1)
            {
                throw new Mp4FormatException("A supported trex sample-description index must be one.");
            }

            result.Add(trackId, new FragmentDefaults(
                U32(data, trex.PayloadStart + 12),
                U32(data, trex.PayloadStart + 16),
                U32(data, trex.PayloadStart + 20)));
        }

        foreach (var trackId in trackById.Keys)
        {
            if (!result.ContainsKey(trackId))
            {
                throw new Mp4FormatException("The initial movie is missing trex defaults for a supported track.");
            }
        }

        return result;
    }

    private static uint ParseFragmentSequence(byte[] data, IList<Box> moofChildren)
    {
        var mfhd = FindSingle(moofChildren, "mfhd");
        RequirePayload(mfhd, 8, "mfhd");
        return U32(data, mfhd.PayloadStart + 4);
    }

    private static void ParseMovieFragment(
        byte[] data,
        Box moof,
        Box mdat,
        IList<Box> moofChildren,
        IDictionary<uint, ParsedTrack> trackById,
        IDictionary<uint, FragmentDefaults> defaults,
        IDictionary<uint, long> decodeEnds,
        ref int totalSamples)
    {
        var trafs = FilterBoxes(moofChildren, "traf");
        if (trafs.Count == 0)
        {
            throw new Mp4FormatException(
                "A movie fragment must contain at least one traf box.");
        }

        var mappedTracks = new HashSet<uint>();
        var ranges = new List<SampleRange>();
        foreach (var traf in trafs)
        {
            var trafChildren = ReadBoxes(
                data,
                traf.PayloadStart,
                traf.End,
                "trun",
                MaximumTrackRunsPerFragment,
                "A track fragment trun count exceeds the supported limit of " +
                MaximumTrackRunsPerFragment + ".");
            var tfhd = FindSingle(trafChildren, "tfhd");
            var header = ParseTrackFragmentHeader(data, tfhd, moof);
            ParsedTrack track;
            if (!trackById.TryGetValue(header.TrackId, out track!))
            {
                throw new Mp4FormatException("A movie fragment references an unknown track ID.");
            }

            if (!mappedTracks.Add(header.TrackId))
            {
                throw new Mp4FormatException("A movie fragment maps the same supported track more than once.");
            }

            var tfdt = FindSingle(trafChildren, "tfdt");
            var decodeTime = ParseBaseDecodeTime(data, tfdt);
            long priorDecodeEnd;
            if (decodeEnds.TryGetValue(header.TrackId, out priorDecodeEnd) && decodeTime < priorDecodeEnd)
            {
                throw new Mp4FormatException("A movie fragment decode time regresses for track " + header.TrackId + ".");
            }

            var runs = FilterBoxes(trafChildren, "trun");
            if (runs.Count == 0)
            {
                throw new Mp4FormatException("A track fragment must contain at least one trun box.");
            }

            long? nextDataOffset = null;
            foreach (var run in runs)
            {
                ParseTrackRun(
                    data,
                    moof,
                    mdat,
                    run,
                    header,
                    defaults[header.TrackId],
                    track,
                    ref decodeTime,
                    ref nextDataOffset,
                    ranges,
                    ref totalSamples);
            }

            decodeEnds[header.TrackId] = decodeTime;
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Start < ranges[index - 1].End)
            {
                throw new Mp4FormatException("Movie fragment sample data ranges overlap.");
            }
        }
    }

    private static TrackFragmentHeader ParseTrackFragmentHeader(byte[] data, Box tfhd, Box moof)
    {
        RequirePayload(tfhd, 8, "tfhd");
        var flags = FullBoxFlags(data, tfhd);
        if ((flags & 0x000001) != 0 && (flags & 0x020000) != 0)
        {
            throw new Mp4FormatException(
                "tfhd base-data-offset and default-base-is-moof must not both be present.");
        }

        var cursor = tfhd.PayloadStart + 4;
        var trackId = U32(data, cursor);
        cursor += 4;
        long baseDataOffset;
        if ((flags & 0x000001) != 0)
        {
            RequireAvailable(tfhd, cursor, 8, "tfhd base-data-offset");
            var rawBase = U64(data, cursor);
            if (rawBase > long.MaxValue) throw new Mp4FormatException("tfhd base-data-offset exceeds the supported range.");
            baseDataOffset = (long)rawBase;
            cursor += 8;
        }
        else if ((flags & 0x020000) != 0)
        {
            baseDataOffset = moof.Start;
        }
        else
        {
            throw new Mp4FormatException("tfhd must define base-data-offset or default-base-is-moof.");
        }

        if ((flags & 0x000002) != 0)
        {
            RequireAvailable(tfhd, cursor, 4, "tfhd sample-description-index");
            if (U32(data, cursor) != 1) throw new Mp4FormatException("tfhd sample-description-index must be one.");
            cursor += 4;
        }

        uint? duration = null;
        uint? size = null;
        uint? sampleFlags = null;
        if ((flags & 0x000008) != 0)
        {
            RequireAvailable(tfhd, cursor, 4, "tfhd default duration");
            duration = U32(data, cursor);
            cursor += 4;
        }

        if ((flags & 0x000010) != 0)
        {
            RequireAvailable(tfhd, cursor, 4, "tfhd default size");
            size = U32(data, cursor);
            cursor += 4;
        }

        if ((flags & 0x000020) != 0)
        {
            RequireAvailable(tfhd, cursor, 4, "tfhd default flags");
            sampleFlags = U32(data, cursor);
        }

        return new TrackFragmentHeader(trackId, baseDataOffset, duration, size, sampleFlags);
    }

    private static long ParseBaseDecodeTime(byte[] data, Box tfdt)
    {
        RequirePayload(tfdt, 8, "tfdt");
        var version = data[tfdt.PayloadStart];
        if (version == 0) return U32(data, tfdt.PayloadStart + 4);
        if (version == 1)
        {
            RequirePayload(tfdt, 12, "tfdt");
            var value = U64(data, tfdt.PayloadStart + 4);
            if (value > long.MaxValue) throw new Mp4FormatException("tfdt decode time exceeds the supported range.");
            return (long)value;
        }

        throw new Mp4FormatException("Unsupported tfdt version.");
    }

    private static void ParseTrackRun(
        byte[] data,
        Box moof,
        Box mdat,
        Box trun,
        TrackFragmentHeader header,
        FragmentDefaults trex,
        ParsedTrack track,
        ref long decodeTime,
        ref long? nextDataOffset,
        IList<SampleRange> ranges,
        ref int totalSamples)
    {
        RequirePayload(trun, 8, "trun");
        var version = data[trun.PayloadStart];
        if (version != 0 && version != 1) throw new Mp4FormatException("Unsupported trun version.");
        var flags = FullBoxFlags(data, trun);
        var count = U32(data, trun.PayloadStart + 4);
        if (count > MaximumSampleCount - totalSamples)
        {
            throw new Mp4FormatException(
                "Fragment sample expansion exceeds the supported limit of " +
                MaximumSampleCount + ".");
        }

        var cursor = trun.PayloadStart + 8;
        long runDataOffset;
        if ((flags & 0x000001) != 0)
        {
            RequireAvailable(trun, cursor, 4, "trun data_offset");
            runDataOffset = checked(header.BaseDataOffset + I32(data, cursor));
            cursor += 4;
        }
        else if (nextDataOffset.HasValue)
        {
            runDataOffset = nextDataOffset.Value;
        }
        else
        {
            runDataOffset = header.BaseDataOffset;
        }

        uint? firstSampleFlags = null;
        if ((flags & 0x000004) != 0 && (flags & 0x000400) != 0)
        {
            throw new Mp4FormatException(
                "trun first_sample_flags and per-sample flags must not both be present.");
        }

        if ((flags & 0x000004) != 0)
        {
            RequireAvailable(trun, cursor, 4, "trun first_sample_flags");
            firstSampleFlags = U32(data, cursor);
            cursor += 4;
        }

        var perSampleBytes = 0;
        if ((flags & 0x000100) != 0) perSampleBytes += 4;
        if ((flags & 0x000200) != 0) perSampleBytes += 4;
        if ((flags & 0x000400) != 0) perSampleBytes += 4;
        if ((flags & 0x000800) != 0) perSampleBytes += 4;
        if (perSampleBytes != 0 && (long)count > (trun.End - cursor) / perSampleBytes)
        {
            throw new Mp4FormatException("trun sample entries exceed the box boundary.");
        }

        var sampleOffset = runDataOffset;
        for (uint sampleIndex = 0; sampleIndex < count; sampleIndex++)
        {
            var duration = ResolveRunValue(data, trun, flags, 0x000100, ref cursor, header.Duration, trex.Duration, "duration");
            var size = ResolveRunValue(data, trun, flags, 0x000200, ref cursor, header.Size, trex.Size, "size");
            uint? inlineSampleFlags = null;
            if ((flags & 0x000400) != 0)
            {
                RequireAvailable(trun, cursor, 4, "trun sample flags");
                inlineSampleFlags = U32(data, cursor);
                cursor += 4;
            }
            var sampleFlags = FragmentDefaultsResolver.ResolveFlags(
                inlineSampleFlags,
                firstSampleFlags,
                sampleIndex,
                header.SampleFlags,
                trex.SampleFlags);

            long compositionOffset = 0;
            if ((flags & 0x000800) != 0)
            {
                RequireAvailable(trun, cursor, 4, "trun composition offset");
                compositionOffset = version == 1 ? I32(data, cursor) : U32(data, cursor);
                cursor += 4;
            }

            if (duration == 0) throw new Mp4FormatException("A fragment sample duration is unresolved or zero.");
            if (size == 0) throw new Mp4FormatException("A fragment sample size is unresolved or zero.");
            var sampleEnd = checked(sampleOffset + size);
            if (sampleOffset < mdat.PayloadStart || sampleEnd > mdat.End)
            {
                throw new Mp4FormatException("A fragment sample range is not fully contained in its mdat payload.");
            }

            ranges.Add(new SampleRange(sampleOffset, sampleEnd));
            var pts = checked(decodeTime + compositionOffset);
            var keyframe = (sampleFlags & 0x00010000U) == 0;
            track.Samples.Add(new ParsedSample(sampleOffset, size, pts, decodeTime, duration, keyframe));
            decodeTime = checked(decodeTime + duration);
            sampleOffset = sampleEnd;
            totalSamples++;
        }

        nextDataOffset = sampleOffset;
    }

    private static uint ResolveRunValue(
        byte[] data,
        Box trun,
        uint flags,
        uint fieldFlag,
        ref long cursor,
        uint? tfhdValue,
        uint trexValue,
        string fieldName)
    {
        if ((flags & fieldFlag) != 0)
        {
            RequireAvailable(trun, cursor, 4, "trun sample " + fieldName);
            var value = U32(data, cursor);
            cursor += 4;
            return value;
        }

        return FragmentDefaultsResolver.ResolveValue(
            null,
            tfhdValue,
            trexValue == 0 ? (uint?)null : trexValue,
            fieldName);
    }

    private static uint FullBoxFlags(byte[] data, Box box)
    {
        RequirePayload(box, 4, box.Type);
        var offset = box.PayloadStart + 1;
        return ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];
    }

    private static void RequireAvailable(Box box, long cursor, int bytes, string field)
    {
        if (cursor < box.PayloadStart || cursor > box.End - bytes)
        {
            throw new Mp4FormatException(field + " exceeds its box boundary.");
        }
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
            try
            {
                var configuration = entry.Type == "avc1"
                    ? ParseAvcConfiguration(data, codecBox, width, height)
                    : ParseHevcConfiguration(data, codecBox, width, height);
                return new SampleDescription(configuration, null);
            }
            catch (ArgumentException ex)
            {
                throw new Mp4FormatException("The MP4 video codec configuration is malformed or unsupported.", ex);
            }
        }

        if (entry.Type != "mp4a")
        {
            throw new Mp4FormatException("Unsupported audio sample entry: " + entry.Type + ".");
        }

        RequirePayload(entry, 28, "mp4a");
        var child = ReadBoxes(data, entry.PayloadStart + 28, entry.End);
        var esds = FindSingle(child, "esds");
        var asc = ParseAudioSpecificConfig(data, esds);
        try
        {
            return new SampleDescription(null, AacCodecConfiguration.FromAudioSpecificConfig(asc));
        }
        catch (ArgumentException ex)
        {
            throw new Mp4FormatException("The AAC AudioSpecificConfig is malformed or unsupported.", ex);
        }
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
        var descriptorCount = 0;
        var result = FindDescriptor(data, esds.PayloadStart + 4, esds.End, 0x05, 0, ref descriptorCount);
        if (result == null) throw new Mp4FormatException("esds does not contain DecoderSpecificInfo.");
        return Slice(data, result.Value.Start, result.Value.Length);
    }

    private static DescriptorRange? FindDescriptor(
        byte[] data,
        long start,
        long end,
        byte wantedTag,
        int depth,
        ref int descriptorCount)
    {
        if (depth > MaximumDescriptorDepth)
        {
            throw new Mp4FormatException(
                "MPEG-4 descriptor nesting exceeds the supported depth of " +
                MaximumDescriptorDepth + ".");
        }

        var cursor = start;
        while (cursor < end)
        {
            descriptorCount++;
            if (descriptorCount > MaximumDescriptorCount)
            {
                throw new Mp4FormatException(
                    "MPEG-4 descriptor traversal exceeds the supported count of " +
                    MaximumDescriptorCount + ".");
            }

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
                var nested = FindDescriptor(
                    data,
                    nestedStart,
                    payloadEnd,
                    wantedTag,
                    depth + 1,
                    ref descriptorCount);
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
        RequirePayload(mdhd, 4, "mdhd");
        var version = data[mdhd.PayloadStart];
        long offset;
        if (version == 0)
        {
            RequirePayload(mdhd, 16, "mdhd");
            offset = mdhd.PayloadStart + 12;
        }
        else if (version == 1)
        {
            RequirePayload(mdhd, 24, "mdhd");
            offset = mdhd.PayloadStart + 20;
        }
        else
        {
            throw new Mp4FormatException("Unsupported mdhd version.");
        }

        return U32(data, offset);
    }

    private static IList<long> ParseTimeToSample(byte[] data, Box box, int expectedSampleCount)
    {
        RequirePayload(box, 8, "stts");
        var count = U32(data, box.PayloadStart + 4);
        var offset = box.PayloadStart + 8;
        var entryCount = ValidateTableEntryCount(count, box, offset, 8, "stts");
        var result = new List<long>(expectedSampleCount);
        for (var i = 0; i < entryCount; i++)
        {
            var sampleCount = U32(data, offset);
            var duration = U32(data, offset + 4);
            if (sampleCount == 0 || sampleCount > expectedSampleCount - result.Count)
            {
                throw new Mp4FormatException("stts sample count does not match the declared stsz sample count.");
            }

            for (uint sample = 0; sample < sampleCount; sample++) result.Add(duration);
            offset += 8;
        }

        if (result.Count != expectedSampleCount)
        {
            throw new Mp4FormatException("stts and stsz describe different sample counts.");
        }

        return result;
    }

    private static IList<long> ParseSampleSizes(byte[] data, Box box)
    {
        RequirePayload(box, 12, "stsz");
        var constantSize = U32(data, box.PayloadStart + 4);
        var count = U32(data, box.PayloadStart + 8);
        if (count > MaximumSampleCount)
        {
            throw new Mp4FormatException(
                "stsz sample count exceeds the supported limit of " +
                MaximumSampleCount + ".");
        }

        var offset = box.PayloadStart + 12;
        if (constantSize == 0 && (long)count > (box.End - offset) / 4)
        {
            throw new Mp4FormatException("stsz entries exceed the box boundary.");
        }

        var sampleCount = checked((int)count);
        var result = new List<long>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            var size = constantSize;
            if (constantSize == 0)
            {
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
        var offset = box.PayloadStart + 8;
        var entryCount = ValidateTableEntryCount(count, box, offset, entrySize, box.Type);
        var result = new List<long>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            result.Add(entrySize == 8
                ? SampleTableDecisions.ToReaderChunkOffset(U64(data, offset))
                : U32(data, offset));
            offset += entrySize;
        }

        return result;
    }

    private static IList<SampleToChunkEntry> ParseSampleToChunk(byte[] data, Box box)
    {
        RequirePayload(box, 8, "stsc");
        var count = U32(data, box.PayloadStart + 4);
        var offset = box.PayloadStart + 8;
        var entryCount = ValidateTableEntryCount(count, box, offset, 12, "stsc");
        var result = new List<SampleToChunkEntry>(entryCount);
        uint previousFirstChunk = 0;
        for (var i = 0; i < entryCount; i++)
        {
            var firstChunk = U32(data, offset);
            var samplesPerChunk = U32(data, offset + 4);
            var description = U32(data, offset + 8);
            if (firstChunk == 0 || samplesPerChunk == 0 || description != 1) throw new Mp4FormatException("stsc contains an unsupported chunk mapping.");
            previousFirstChunk = SampleTableDecisions.ValidateNextSampleToChunkFirstChunk(
                firstChunk,
                previousFirstChunk,
                i == 0);
            result.Add(new SampleToChunkEntry(firstChunk, samplesPerChunk));
            offset += 12;
        }

        return result;
    }

    private static IList<long> ParseCompositionOffsets(byte[] data, Box? box, int expectedSampleCount)
    {
        if (box == null) return new List<long>();
        var value = box;
        RequirePayload(value, 8, "ctts");
        var version = data[value.PayloadStart];
        if (version != 0 && version != 1) throw new Mp4FormatException("Unsupported ctts version.");
        var count = U32(data, value.PayloadStart + 4);
        var offset = value.PayloadStart + 8;
        var entryCount = ValidateTableEntryCount(count, value, offset, 8, "ctts");
        var result = new List<long>(expectedSampleCount);
        for (var i = 0; i < entryCount; i++)
        {
            var sampleCount = U32(data, offset);
            var compositionOffset = version == 1 ? I32(data, offset + 4) : U32(data, offset + 4);
            if (sampleCount == 0 || sampleCount > expectedSampleCount - result.Count)
            {
                throw new Mp4FormatException("ctts sample count does not match the declared stsz sample count.");
            }

            for (uint sample = 0; sample < sampleCount; sample++) result.Add(compositionOffset);
            offset += 8;
        }

        if (result.Count != expectedSampleCount)
        {
            throw new Mp4FormatException("ctts does not describe every sample.");
        }

        return result;
    }

    private static ISet<int>? ParseSyncSamples(byte[] data, Box? box, int expectedSampleCount)
    {
        if (box == null) return null;
        var value = box;
        RequirePayload(value, 8, "stss");
        var count = U32(data, value.PayloadStart + 4);
        var offset = value.PayloadStart + 8;
        var entryCount = ValidateTableEntryCount(count, value, offset, 4, "stss");
        if (entryCount > expectedSampleCount)
        {
            throw new Mp4FormatException("stss contains more sync samples than stsz declares.");
        }

        var result = new HashSet<int>();
        for (var i = 0; i < entryCount; i++)
        {
            var sampleNumber = U32(data, offset);
            if (sampleNumber == 0 || sampleNumber > expectedSampleCount)
            {
                throw new Mp4FormatException("stss contains an invalid sample number.");
            }

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

        var result = new List<ParsedSample>(sizes.Count);
        var sampleIndex = 0;
        var mapIndex = 0;
        var mapping = chunkMap[0];
        for (var chunkIndex = 1; chunkIndex <= chunkOffsets.Count; chunkIndex++)
        {
            while (mapIndex + 1 < chunkMap.Count && chunkMap[mapIndex + 1].FirstChunk <= chunkIndex)
            {
                mapIndex++;
                mapping = chunkMap[mapIndex];
            }

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
        return EncodedVideoNalUnit.FromOwnedData(
            nal,
            MediaTime.FromTicksRounded(sample.Pts, track.Timescale),
            MediaTime.FromTicksRounded(sample.Dts, track.Timescale),
            MediaTime.FromTicksRounded(sample.Duration, track.Timescale),
            sample.IsKeyFrame);
    }

    private static EncodedAudioSample CreateAudioSample(ParsedSample sample, ParsedTrack track)
    {
        return EncodedAudioSample.FromOwnedData(
            Slice(track.Data, sample.Offset, checked((int)sample.Size)),
            MediaTime.FromTicksRounded(sample.Pts, track.Timescale),
            MediaTime.FromTicksRounded(sample.Dts, track.Timescale),
            MediaTime.FromTicksRounded(sample.Duration, track.Timescale));
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
        return FilterBoxes(children, requiredType);
    }

    private static IList<Box> FilterBoxes(IList<Box> boxes, string requiredType)
    {
        var result = new List<Box>();
        foreach (var box in boxes) if (box.Type == requiredType) result.Add(box);
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
        return ReadBoxes(data, start, end, null, 0, null);
    }

    private static IList<Box> ReadBoxes(
        byte[] data,
        long start,
        long end,
        string? countedType,
        int maximumCount,
        string? limitMessage)
    {
        if (start < 0 || end < start || end > data.LongLength) throw new Mp4FormatException("An MP4 box range is outside the stream boundary.");
        var result = new List<Box>();
        var counted = 0;
        var cursor = start;
        while (cursor < end)
        {
            if (result.Count >= MaximumBoxesPerContainer)
            {
                throw new Mp4FormatException(
                    "An MP4 container exceeds the supported box count of " +
                    MaximumBoxesPerContainer + ".");
            }

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
            if (countedType != null && type == countedType)
            {
                counted++;
                if (counted > maximumCount)
                {
                    throw new Mp4FormatException(limitMessage ?? "An MP4 box type exceeds its supported count.");
                }
            }

            result.Add(new Box(cursor, size, header, type));
            cursor += size;
        }

        return result;
    }

    private static int ValidateTableEntryCount(
        uint count,
        Box box,
        long entriesStart,
        int entrySize,
        string tableName)
    {
        if (count > MaximumTableEntryCount)
        {
            throw new Mp4FormatException(
                tableName + " entry count exceeds the supported limit of " +
                MaximumTableEntryCount + ".");
        }

        if ((long)count > (box.End - entriesStart) / entrySize)
        {
            throw new Mp4FormatException(tableName + " entries exceed the box boundary.");
        }

        return checked((int)count);
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
        public ParsedTrack(byte[] data, uint trackId, string handlerType, long timescale, VideoCodecConfiguration? videoConfiguration, AacCodecConfiguration? audioConfiguration, IList<ParsedSample> samples)
        {
            Data = data;
            TrackId = trackId;
            HandlerType = handlerType;
            Timescale = checked((int)timescale);
            VideoConfiguration = videoConfiguration;
            AudioConfiguration = audioConfiguration;
            Samples = samples;
        }

        public uint TrackId { get; }
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

    private sealed class FragmentDefaults
    {
        public FragmentDefaults(uint duration, uint size, uint sampleFlags)
        {
            Duration = duration;
            Size = size;
            SampleFlags = sampleFlags;
        }

        public uint Duration { get; }
        public uint Size { get; }
        public uint SampleFlags { get; }
    }

    private sealed class TrackFragmentHeader
    {
        public TrackFragmentHeader(
            uint trackId,
            long baseDataOffset,
            uint? duration,
            uint? size,
            uint? sampleFlags)
        {
            TrackId = trackId;
            BaseDataOffset = baseDataOffset;
            Duration = duration;
            Size = size;
            SampleFlags = sampleFlags;
        }

        public uint TrackId { get; }
        public long BaseDataOffset { get; }
        public uint? Duration { get; }
        public uint? Size { get; }
        public uint? SampleFlags { get; }
    }

    private readonly struct SampleRange
    {
        public SampleRange(long start, long end)
        {
            Start = start;
            End = end;
        }

        public long Start { get; }
        public long End { get; }
    }

    private readonly struct DescriptorRange
    {
        public DescriptorRange(long start, int length) { Start = start; Length = length; }
        public long Start { get; }
        public int Length { get; }
    }
}

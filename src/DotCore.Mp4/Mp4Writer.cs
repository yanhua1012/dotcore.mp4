using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DotCore.Mp4;

/// <summary>將 progressive、faststart 或 fragmented MP4 寫入 caller-owned stream。</summary>
public sealed class Mp4Writer : IDisposable
{
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly Mp4WriteMode _mode;
    private readonly int _maximumFragmentBufferBytes;
    private readonly List<Mp4Sample> _videoSamples = new List<Mp4Sample>();
    private readonly List<Mp4Sample> _audioSamples = new List<Mp4Sample>();
    private readonly List<FragmentSample> _fragmentSamples = new List<FragmentSample>();
    private readonly long _mdatStart;
    private PendingVideoAccessUnit? _pendingVideo;
    private long? _lastVideoDts;
    private long? _lastAudioDts;
    private long? _lastGlobalDts;
    private VideoCodecConfiguration? _videoConfiguration;
    private AacCodecConfiguration? _audioConfiguration;
    private bool _finalized;
    private bool _disposed;
    private bool _fragmentedStarted;
    private bool _fragmentedHasVideoSample;
    private int _fragmentBufferedBytes;
    private uint _fragmentSequenceNumber = 1;

    public Mp4Writer(Stream output, bool leaveOpen = true)
        : this(output, new Mp4WriterOptions(), leaveOpen)
    {
    }

    /// <summary>以指定的輸出選項建立 MP4 writer。</summary>
    /// <param name="output">接收 MP4 資料的 caller-owned stream。</param>
    /// <param name="options">輸出模式與資源限制；writer 會在建構時複製其值。</param>
    /// <param name="leaveOpen">writer 釋放時是否保持 <paramref name="output"/> 開啟。</param>
    public Mp4Writer(Stream output, Mp4WriterOptions options, bool leaveOpen = true)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Mode != Mp4WriteMode.Progressive &&
            options.Mode != Mp4WriteMode.FastStart &&
            options.Mode != Mp4WriteMode.Fragmented)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Mp4WriterOptions.Mode),
                options.Mode,
                "MP4 write mode must be Progressive, FastStart, or Fragmented.");
        }

        if (options.MaximumFragmentBufferBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Mp4WriterOptions.MaximumFragmentBufferBytes),
                options.MaximumFragmentBufferBytes,
                "Maximum fragment buffer bytes must be greater than zero.");
        }

        ValidateOutputCapabilities(output, options.Mode);

        _output = output;
        _leaveOpen = leaveOpen;
        _mode = options.Mode;
        _maximumFragmentBufferBytes = options.MaximumFragmentBufferBytes;

        if (_mode == Mp4WriteMode.Fragmented)
        {
            _mdatStart = -1;
            return;
        }

        var writer = new IsoBmffWriter(_output);
        WriteFileTypeBox(writer);
        _mdatStart = _output.Position;
        writer.WriteUInt32(1);
        writer.WriteFourCc("mdat");
        writer.WriteUInt64(0);
    }

    private static void ValidateOutputCapabilities(Stream output, Mp4WriteMode mode)
    {
        if (!output.CanWrite)
        {
            throw new InvalidOperationException("MP4 output requires a writable stream; no MP4 header was emitted.");
        }

        if (mode == Mp4WriteMode.Fragmented)
        {
            return;
        }

        if (!output.CanSeek)
        {
            throw new InvalidOperationException("Progressive and faststart MP4 output requires a seekable stream; no MP4 header was emitted.");
        }

        if (mode != Mp4WriteMode.FastStart)
        {
            return;
        }

        if (!output.CanRead)
        {
            throw new InvalidOperationException("Faststart MP4 output requires a readable stream; no MP4 header was emitted.");
        }

        try
        {
            var length = output.Length;
            var position = output.Position;
            output.SetLength(length);
            output.Position = position;
        }
        catch (Exception error) when (
            error is NotSupportedException ||
            error is InvalidOperationException ||
            error is IOException)
        {
            throw new InvalidOperationException(
                "Faststart MP4 output requires a stream that supports changing its length; no MP4 header was emitted.",
                error);
        }
    }

    public VideoCodecConfiguration? VideoConfiguration => _videoConfiguration;
    public AacCodecConfiguration? AudioConfiguration => _audioConfiguration;

    public void ConfigureVideo(VideoCodecConfiguration configuration) => SetVideoCodecConfiguration(configuration);

    public void SetVideoConfiguration(VideoCodecConfiguration configuration) => SetVideoCodecConfiguration(configuration);

    public void SetVideoCodecConfiguration(VideoCodecConfiguration configuration)
    {
        EnsureWritable();
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        if (_videoConfiguration != null || _videoSamples.Count != 0 || _pendingVideo != null || _fragmentedStarted)
        {
            throw new InvalidOperationException("The video codec configuration can only be set once before video samples are written.");
        }

        _videoConfiguration = configuration;
    }

    public void ConfigureAudio(AacCodecConfiguration configuration) => SetAudioCodecConfiguration(configuration);

    public void SetAudioConfiguration(AacCodecConfiguration configuration) => SetAudioCodecConfiguration(configuration);

    public void SetAudioCodecConfiguration(AacCodecConfiguration configuration)
    {
        EnsureWritable();
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        if (_audioConfiguration != null || _audioSamples.Count != 0 || _fragmentedStarted)
        {
            throw new InvalidOperationException("The AAC codec configuration can only be set once before audio samples are written.");
        }

        _audioConfiguration = configuration;
    }

    public void WriteVideo(EncodedVideoNalUnit sample) => WriteVideoNalUnit(sample);

    public void WriteVideoNalUnit(EncodedVideoNalUnit sample)
    {
        EnsureWritable();
        if (_videoConfiguration == null)
        {
            throw new InvalidOperationException("Configure a video codec before writing video NAL units.");
        }

        if (sample == null) throw new ArgumentNullException(nameof(sample));
        var pts = MediaTime.ToTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale);
        var dts = MediaTime.ToTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale);
        var duration = MediaTime.ToTicks(sample.Duration, MediaTime.DefaultTrackTimescale);
        ValidateTimedSample(dts, duration, _lastVideoDts, "video");
        var nalUnits = NalUnits.Normalize(sample.DataBytes);
        ValidateNalLengths(nalUnits, _videoConfiguration.NalLengthSize);
        if (_mode == Mp4WriteMode.Fragmented)
        {
            WriteFragmentedVideoNalUnits(sample, nalUnits, pts, dts, duration);
            return;
        }

        _lastVideoDts = dts;
        if (_pendingVideo != null && _pendingVideo.Pts == pts && _pendingVideo.Dts == dts)
        {
            if (_pendingVideo.Duration != duration || _pendingVideo.IsKeyFrame != sample.IsKeyFrame)
            {
                throw new Mp4FormatException("NAL units in one access unit must have the same duration and key-frame state.");
            }

            _pendingVideo.Nals.AddRange(nalUnits);
            return;
        }

        FlushPendingVideo();
        _pendingVideo = new PendingVideoAccessUnit(pts, dts, duration, sample.IsKeyFrame, nalUnits);
    }

    public void WriteAudio(EncodedAudioSample sample) => WriteAudioSample(sample);

    public void WriteAacSample(EncodedAudioSample sample) => WriteAudioSample(sample);

    public void WriteAudioSample(EncodedAudioSample sample)
    {
        EnsureWritable();
        if (_audioConfiguration == null)
        {
            throw new InvalidOperationException("Configure AAC before writing audio samples.");
        }

        if (sample == null) throw new ArgumentNullException(nameof(sample));
        var pts = MediaTime.ToTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale);
        var dts = MediaTime.ToTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale);
        var duration = MediaTime.ToTicks(sample.Duration, MediaTime.DefaultTrackTimescale);
        ValidateTimedSample(dts, duration, _lastAudioDts, "audio");
        if (_mode == Mp4WriteMode.Fragmented)
        {
            if (_videoConfiguration == null)
            {
                throw new InvalidOperationException("Fragmented MP4 output requires video configuration before AAC media.");
            }

            ValidateGlobalDts(dts, "audio");
            var data = sample.DataBytes;
            EnsureFragmentBufferCapacity(data.Length);
            EnsureFragmentedStarted();
            _fragmentSamples.Add(new FragmentSample(
                false,
                FragmentPayloadSource.FromAudio(data),
                pts,
                dts,
                duration,
                true));
            _fragmentBufferedBytes = checked(_fragmentBufferedBytes + data.Length);
            _lastAudioDts = dts;
            _lastGlobalDts = dts;
            return;
        }

        _lastAudioDts = dts;
        var offset = _output.Position;
        _output.Write(sample.DataBytes, 0, sample.DataBytes.Length);
        _audioSamples.Add(new Mp4Sample(offset, sample.DataBytes.Length, pts, dts, duration, true));
    }

    /// <summary>Backpatches mdat and appends the complete movie metadata.</summary>
    public void FinalizeFile()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Mp4Writer));
        if (_finalized) return;

        if (_mode == Mp4WriteMode.Fragmented)
        {
            CommitPendingFragmentedVideo();
            if (!_fragmentedHasVideoSample)
            {
                throw new InvalidOperationException("Fragmented MP4 output requires at least one video sample.");
            }

            FlushFragment(null);
            _finalized = true;
            return;
        }

        FlushPendingVideo();
        if (_videoSamples.Count == 0 && _audioSamples.Count == 0)
        {
            throw new InvalidOperationException("At least one video or AAC sample is required before finalization.");
        }

        var endOfMdat = _output.Position;
        var mdatSize = checked((ulong)(endOfMdat - _mdatStart));
        var restore = _output.Position;
        _output.Seek(_mdatStart, SeekOrigin.Begin);
        var headerWriter = new IsoBmffWriter(_output);
        headerWriter.WriteUInt32(1);
        headerWriter.WriteFourCc("mdat");
        headerWriter.WriteUInt64(mdatSize);
        _output.Seek(restore, SeekOrigin.Begin);

        if (_mode == Mp4WriteMode.FastStart)
        {
            var moov = FastStartLayout.BuildStableMovieBox(BuildMovieBox);
            RelocateMdatForFastStart(endOfMdat, moov);
        }
        else
        {
            var moov = BuildMovieBox(0);
            _output.Write(moov, 0, moov.Length);
        }

        _finalized = true;
    }

    public void Complete() => FinalizeFile();
    public void Finish() => FinalizeFile();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen)
        {
            _output.Dispose();
        }
    }

    private void FlushPendingVideo()
    {
        if (_pendingVideo == null) return;
        if (_mode == Mp4WriteMode.Fragmented)
        {
            CommitPendingFragmentedVideo();
            return;
        }

        if (_videoConfiguration == null)
        {
            throw new InvalidOperationException("Configure a video codec before writing video NAL units.");
        }

        var offset = _output.Position;
        long size = 0;
        var writer = new IsoBmffWriter(_output);
        foreach (var nal in _pendingVideo.Nals)
        {
            if (nal.Length > MaxLengthForNal(_videoConfiguration.NalLengthSize))
            {
                throw new Mp4FormatException("A video NAL unit does not fit in the configured MP4 length field.");
            }

            WriteNalLength(writer, nal.Length, _videoConfiguration.NalLengthSize);
            writer.WriteBytes(nal.BackingArray, nal.Offset, nal.Count);
            size = checked(size + _videoConfiguration.NalLengthSize + nal.Count);
        }

        if (size == 0 || size > uint.MaxValue)
        {
            throw new Mp4FormatException("A video access unit has an unsupported MP4 sample size.");
        }

        _videoSamples.Add(new Mp4Sample(
            offset,
            size,
            _pendingVideo.Pts,
            _pendingVideo.Dts,
            _pendingVideo.Duration,
            _pendingVideo.IsKeyFrame));
        _pendingVideo = null;
    }

    private void WriteFragmentedVideoNalUnits(
        EncodedVideoNalUnit sample,
        IList<NalUnitRange> nalUnits,
        long pts,
        long dts,
        long duration)
    {
        ValidateGlobalDts(dts, "video");
        var additionalBytes = GetEncodedVideoSize(nalUnits, _videoConfiguration!.NalLengthSize);
        if (_pendingVideo != null && _pendingVideo.Pts == pts && _pendingVideo.Dts == dts)
        {
            if (_pendingVideo.Duration != duration || _pendingVideo.IsKeyFrame != sample.IsKeyFrame)
            {
                throw new Mp4FormatException("NAL units in one access unit must have the same duration and key-frame state.");
            }

            EnsureFragmentBufferCapacity(additionalBytes);
            _pendingVideo.Nals.AddRange(nalUnits);
            _fragmentBufferedBytes = checked(_fragmentBufferedBytes + additionalBytes);
            _lastVideoDts = dts;
            _lastGlobalDts = dts;
            return;
        }

        if (!_fragmentedHasVideoSample && _pendingVideo == null && !sample.IsKeyFrame)
        {
            throw new InvalidOperationException("The first fragmented video access unit must be a keyframe.");
        }

        CommitPendingFragmentedVideo();
        if (sample.IsKeyFrame && _fragmentedHasVideoSample)
        {
            FlushFragment(dts);
        }

        EnsureFragmentBufferCapacity(additionalBytes);
        EnsureFragmentedStarted();
        _pendingVideo = new PendingVideoAccessUnit(pts, dts, duration, sample.IsKeyFrame, nalUnits);
        _fragmentBufferedBytes = checked(_fragmentBufferedBytes + additionalBytes);
        _lastVideoDts = dts;
        _lastGlobalDts = dts;
    }

    private void EnsureFragmentedStarted()
    {
        if (_fragmentedStarted) return;
        if (_videoConfiguration == null)
        {
            throw new InvalidOperationException("Fragmented MP4 output requires video configuration before media.");
        }

        var fileType = BuildBytes(WriteFileTypeBox);
        var movie = BuildFragmentedInitialMovieBox();
        _output.Write(fileType, 0, fileType.Length);
        _output.Write(movie, 0, movie.Length);
        _fragmentedStarted = true;
    }

    private void EnsureFragmentBufferCapacity(int additionalBytes)
    {
        if (additionalBytes < 0 ||
            additionalBytes > _maximumFragmentBufferBytes - _fragmentBufferedBytes)
        {
            throw new InvalidOperationException(
                "The fragmented MP4 buffer limit was exceeded before the next keyframe.");
        }
    }

    private void ValidateGlobalDts(long dts, string trackName)
    {
        if (_lastGlobalDts.HasValue && dts < _lastGlobalDts.Value)
        {
            throw new Mp4TimestampException(
                trackName + " DTS must not decrease across fragmented video and audio submissions.");
        }
    }

    private static int GetEncodedVideoSize(IList<NalUnitRange> nalUnits, int nalLengthSize)
    {
        ValidateNalLengths(nalUnits, nalLengthSize);
        var size = 0;
        foreach (var nal in nalUnits)
        {
            size = checked(size + nalLengthSize + nal.Length);
        }

        if (size == 0) throw new Mp4FormatException("A video access unit must not be empty.");
        return size;
    }

    private static void ValidateNalLengths(IList<NalUnitRange> nalUnits, int nalLengthSize)
    {
        foreach (var nal in nalUnits)
        {
            if (nal.Count > MaxLengthForNal(nalLengthSize))
            {
                throw new Mp4FormatException("A video NAL unit does not fit in the configured MP4 length field.");
            }
        }
    }

    private void CommitPendingFragmentedVideo()
    {
        if (_pendingVideo == null) return;
        if (_videoConfiguration == null)
        {
            throw new InvalidOperationException("Video configuration is missing.");
        }

        var encodedSize = GetEncodedVideoSize(
            _pendingVideo.Nals,
            _videoConfiguration.NalLengthSize);
        _fragmentSamples.Add(new FragmentSample(
            true,
            FragmentPayloadSource.FromVideo(
                _pendingVideo.Nals,
                _videoConfiguration.NalLengthSize,
                encodedSize),
            _pendingVideo.Pts,
            _pendingVideo.Dts,
            _pendingVideo.Duration,
            _pendingVideo.IsKeyFrame));
        _fragmentedHasVideoSample = true;
        _pendingVideo = null;
    }

    private byte[] BuildFragmentedInitialMovieBox()
    {
        using (var stream = new MemoryStream())
        {
            var writer = new IsoBmffWriter(stream);
            var moov = writer.BeginBox("moov");
            WriteMovieHeader(writer, 0);
            var videoTrackId = 1;
            WriteFragmentedVideoTrack(writer, videoTrackId);
            var audioTrackId = 0;
            if (_audioConfiguration != null)
            {
                audioTrackId = 2;
                WriteFragmentedAudioTrack(writer, audioTrackId);
            }

            WriteMovieExtends(writer, videoTrackId, audioTrackId);
            writer.EndBox(moov);
            return stream.ToArray();
        }
    }

    private void WriteFragmentedVideoTrack(IsoBmffWriter writer, int trackId)
    {
        if (_videoConfiguration == null) throw new InvalidOperationException("Video configuration is missing.");
        var trak = writer.BeginBox("trak");
        WriteTrackHeader(writer, trackId, 0, false, _videoConfiguration.Width, _videoConfiguration.Height);
        var mdia = writer.BeginBox("mdia");
        WriteMediaHeader(writer, 0);
        WriteHandler(writer, "vide", "DotCore MP4 Video");
        var minf = writer.BeginBox("minf");
        var vmhd = writer.BeginBox("vmhd");
        WriteFullBoxHeader(writer, 0, 1);
        writer.WriteZeros(8);
        writer.EndBox(vmhd);
        WriteDataInformation(writer);
        WriteSampleTable(writer, true, Array.Empty<Mp4Sample>(), _videoConfiguration, null, 0);
        writer.EndBox(minf);
        writer.EndBox(mdia);
        writer.EndBox(trak);
    }

    private void WriteFragmentedAudioTrack(IsoBmffWriter writer, int trackId)
    {
        if (_audioConfiguration == null) throw new InvalidOperationException("AAC configuration is missing.");
        var trak = writer.BeginBox("trak");
        WriteTrackHeader(writer, trackId, 0, true, 0, 0);
        var mdia = writer.BeginBox("mdia");
        WriteMediaHeader(writer, 0);
        WriteHandler(writer, "soun", "DotCore MP4 AAC Audio");
        var minf = writer.BeginBox("minf");
        var smhd = writer.BeginBox("smhd");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteInt16(0);
        writer.WriteUInt16(0);
        writer.EndBox(smhd);
        WriteDataInformation(writer);
        WriteSampleTable(writer, false, Array.Empty<Mp4Sample>(), null, _audioConfiguration, 0);
        writer.EndBox(minf);
        writer.EndBox(mdia);
        writer.EndBox(trak);
    }

    private static void WriteMovieExtends(IsoBmffWriter writer, int videoTrackId, int audioTrackId)
    {
        var mvex = writer.BeginBox("mvex");
        WriteTrackExtends(writer, videoTrackId);
        if (audioTrackId != 0) WriteTrackExtends(writer, audioTrackId);
        writer.EndBox(mvex);
    }

    private static void WriteTrackExtends(IsoBmffWriter writer, int trackId)
    {
        var trex = writer.BeginBox("trex");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32((uint)trackId);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.EndBox(trex);
    }

    private void FlushFragment(long? boundaryDts)
    {
        var selected = new List<FragmentSample>();
        foreach (var sample in _fragmentSamples)
        {
            if (!boundaryDts.HasValue || sample.Dts < boundaryDts.Value)
            {
                selected.Add(sample);
            }
        }

        if (selected.Count == 0) return;
        if (!selected.Any(sample => sample.Video))
        {
            if (!boundaryDts.HasValue)
            {
                throw new InvalidOperationException("A fragmented MP4 fragment must contain video.");
            }

            return;
        }

        var firstVideo = selected.First(sample => sample.Video);
        if (!firstVideo.IsKeyFrame)
        {
            throw new InvalidOperationException("A fragmented MP4 fragment must start with a video keyframe.");
        }

        var video = selected.Where(sample => sample.Video).ToList();
        var audio = selected.Where(sample => !sample.Video).ToList();
        var videoPayloadBytes = video.Sum(sample => (long)sample.EncodedSize);
        var audioPayloadBytes = audio.Sum(sample => (long)sample.EncodedSize);
        var payloadBytes = checked(videoPayloadBytes + audioPayloadBytes);
        var mdatSize = checked(payloadBytes + 8);
        if (mdatSize > uint.MaxValue)
        {
            throw new Mp4FormatException("A fragment mdat box exceeds the 32-bit box-size limit.");
        }

        uint nextSequenceNumber;
        try
        {
            nextSequenceNumber = checked(_fragmentSequenceNumber + 1);
        }
        catch (OverflowException error)
        {
            throw new Mp4FormatException("The fragment sequence number exceeds the supported range.", error);
        }

        var moof = BuildMovieFragment(video, audio, videoPayloadBytes);

        _output.Write(moof.Buffer, 0, moof.Count);
        var outputWriter = new IsoBmffWriter(_output);
        outputWriter.WriteUInt32((uint)mdatSize);
        outputWriter.WriteFourCc("mdat");
        foreach (var sample in video) WriteFragmentPayload(sample);
        foreach (var sample in audio) WriteFragmentPayload(sample);

        foreach (var sample in selected)
        {
            _fragmentSamples.Remove(sample);
            _fragmentBufferedBytes = checked(_fragmentBufferedBytes - sample.EncodedSize);
        }

        _fragmentedHasVideoSample = _fragmentSamples.Any(sample => sample.Video);
        _fragmentSequenceNumber = nextSequenceNumber;
    }

    private BufferSegment BuildMovieFragment(
        IList<FragmentSample> video,
        IList<FragmentSample> audio,
        long videoPayloadBytes)
    {
        var trackCount = (video.Count == 0 ? 0 : 1) + (audio.Count == 0 ? 0 : 1);
        int capacity;
        try
        {
            capacity = checked(128 + ((video.Count + audio.Count) * 20) + (trackCount * 80));
        }
        catch (OverflowException error)
        {
            throw new Mp4FormatException("Fragment metadata capacity exceeds the supported range.", error);
        }

        using (var stream = new MemoryStream(capacity))
        {
            var writer = new IsoBmffWriter(stream);
            var dataOffsetPatchPositions = new List<long>(trackCount);
            var moof = writer.BeginBox("moof");
            var mfhd = writer.BeginBox("mfhd");
            WriteFullBoxHeader(writer, 0, 0);
            writer.WriteUInt32(_fragmentSequenceNumber);
            writer.EndBox(mfhd);
            if (video.Count != 0) dataOffsetPatchPositions.Add(WriteTrackFragment(writer, 1, video));
            if (audio.Count != 0) dataOffsetPatchPositions.Add(WriteTrackFragment(writer, 2, audio));
            writer.EndBox(moof);
            var length = checked((int)stream.Length);
            var videoDataOffset = checked((long)length + 8);
            var audioDataOffset = checked(videoDataOffset + videoPayloadBytes);
            var patchIndex = 0;
            if (video.Count != 0)
            {
                BigEndianPatch.PatchInt32(
                    stream.GetBuffer(),
                    length,
                    dataOffsetPatchPositions[patchIndex++],
                    videoDataOffset);
            }

            if (audio.Count != 0)
            {
                BigEndianPatch.PatchInt32(
                    stream.GetBuffer(),
                    length,
                    dataOffsetPatchPositions[patchIndex],
                    audioDataOffset);
            }

            return new BufferSegment(stream.GetBuffer(), length);
        }
    }

    private static long WriteTrackFragment(
        IsoBmffWriter writer,
        int trackId,
        IList<FragmentSample> samples)
    {
        var traf = writer.BeginBox("traf");
        var tfhd = writer.BeginBox("tfhd");
        WriteFullBoxHeader(writer, 0, 0x020000);
        writer.WriteUInt32((uint)trackId);
        writer.EndBox(tfhd);
        var tfdt = writer.BeginBox("tfdt");
        WriteFullBoxHeader(writer, 1, 0);
        writer.WriteUInt64(checked((ulong)samples[0].Dts));
        writer.EndBox(tfdt);

        var signedCompositionOffsets = samples.Any(sample => sample.Pts < sample.Dts);
        var trun = writer.BeginBox("trun");
        WriteFullBoxHeader(writer, signedCompositionOffsets ? (byte)1 : (byte)0, 0x000f01);
        writer.WriteUInt32((uint)samples.Count);
        var dataOffsetPatchPosition = writer.Position;
        writer.WriteInt32(0);
        foreach (var sample in samples)
        {
            if (sample.Duration > uint.MaxValue) throw new Mp4FormatException("A fragment sample duration exceeds the trun range.");
            writer.WriteUInt32((uint)sample.Duration);
            writer.WriteUInt32((uint)sample.EncodedSize);
            writer.WriteUInt32(sample.Video
                ? sample.IsKeyFrame ? 0x02000000U : 0x01010000U
                : 0x02000000U);
            var compositionOffset = checked(sample.Pts - sample.Dts);
            if (signedCompositionOffsets)
            {
                if (compositionOffset < int.MinValue || compositionOffset > int.MaxValue)
                {
                    throw new Mp4FormatException("A fragment composition offset exceeds the signed trun range.");
                }

                writer.WriteInt32((int)compositionOffset);
            }
            else
            {
                if (compositionOffset < 0 || compositionOffset > uint.MaxValue)
                {
                    throw new Mp4FormatException("A fragment composition offset exceeds the unsigned trun range.");
                }

                writer.WriteUInt32((uint)compositionOffset);
            }
        }

        writer.EndBox(trun);
        writer.EndBox(traf);
        return dataOffsetPatchPosition;
    }

    private void WriteFragmentPayload(FragmentSample sample)
    {
        var source = sample.PayloadSource;
        if (source.ContiguousData != null)
        {
            _output.Write(source.ContiguousData, 0, source.ContiguousData.Length);
            return;
        }

        var ranges = source.Ranges ??
                     throw new InvalidOperationException("A fragment payload source has no data.");
        var writer = new IsoBmffWriter(_output);
        foreach (var range in ranges)
        {
            WriteNalLength(writer, range.Count, source.NalLengthSize);
            writer.WriteBytes(range.BackingArray, range.Offset, range.Count);
        }
    }

    private void RelocateMdatForFastStart(long endOfMdat, byte[] moov)
    {
        const int relocationBufferBytes = 64 * 1024;
        var destinationEnd = checked(endOfMdat + moov.LongLength);
        _output.SetLength(destinationEnd);
        var buffer = new byte[relocationBufferBytes];
        var sourceEnd = endOfMdat;
        try
        {
            while (sourceEnd > _mdatStart)
            {
                var count = (int)Math.Min(buffer.Length, sourceEnd - _mdatStart);
                var sourceStart = sourceEnd - count;
                _output.Position = sourceStart;
                var read = 0;
                while (read < count)
                {
                    var current = _output.Read(buffer, read, count - read);
                    if (current == 0)
                    {
                        throw new IOException("Faststart mdat relocation encountered an unexpected end of stream.");
                    }

                    read += current;
                }

                _output.Position = checked(sourceStart + moov.LongLength);
                _output.Write(buffer, 0, count);
                sourceEnd = sourceStart;
            }

            _output.Position = _mdatStart;
            _output.Write(moov, 0, moov.Length);
            _output.Position = destinationEnd;
        }
        catch (Exception error) when (
            error is IOException ||
            error is NotSupportedException ||
            error is InvalidOperationException)
        {
            throw new IOException("Faststart mdat relocation failed; the output may be incomplete.", error);
        }
    }

    private byte[] BuildMovieBox(long chunkOffsetAdjustment)
    {
        using (var stream = new MemoryStream())
        {
            var writer = new IsoBmffWriter(stream);
            var moov = writer.BeginBox("moov");
            var duration = Math.Max(GetTrackDuration(_videoSamples), GetTrackDuration(_audioSamples));
            WriteMovieHeader(writer, duration);
            var trackId = 1;
            if (_videoSamples.Count != 0)
            {
                WriteVideoTrack(writer, trackId++, chunkOffsetAdjustment);
            }

            if (_audioSamples.Count != 0)
            {
                WriteAudioTrack(writer, trackId++, chunkOffsetAdjustment);
            }

            writer.EndBox(moov);
            return stream.ToArray();
        }
    }

    private void WriteVideoTrack(IsoBmffWriter writer, int trackId, long chunkOffsetAdjustment)
    {
        if (_videoConfiguration == null) throw new InvalidOperationException("Video configuration is missing.");
        var trak = writer.BeginBox("trak");
        WriteTrackHeader(writer, trackId, GetTrackDuration(_videoSamples), false, _videoConfiguration.Width, _videoConfiguration.Height);
        var mdia = writer.BeginBox("mdia");
        WriteMediaHeader(writer, GetTrackDuration(_videoSamples));
        WriteHandler(writer, "vide", "DotCore MP4 Video");
        var minf = writer.BeginBox("minf");
        var vmhd = writer.BeginBox("vmhd");
        WriteFullBoxHeader(writer, 0, 1);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.EndBox(vmhd);
        WriteDataInformation(writer);
        WriteSampleTable(writer, true, _videoSamples, _videoConfiguration, null, chunkOffsetAdjustment);
        writer.EndBox(minf);
        writer.EndBox(mdia);
        writer.EndBox(trak);
    }

    private void WriteAudioTrack(IsoBmffWriter writer, int trackId, long chunkOffsetAdjustment)
    {
        if (_audioConfiguration == null) throw new InvalidOperationException("AAC configuration is missing.");
        var trak = writer.BeginBox("trak");
        WriteTrackHeader(writer, trackId, GetTrackDuration(_audioSamples), true, 0, 0);
        var mdia = writer.BeginBox("mdia");
        WriteMediaHeader(writer, GetTrackDuration(_audioSamples));
        WriteHandler(writer, "soun", "DotCore MP4 AAC Audio");
        var minf = writer.BeginBox("minf");
        var smhd = writer.BeginBox("smhd");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteInt16(0);
        writer.WriteUInt16(0);
        writer.EndBox(smhd);
        WriteDataInformation(writer);
        WriteSampleTable(writer, false, _audioSamples, null, _audioConfiguration, chunkOffsetAdjustment);
        writer.EndBox(minf);
        writer.EndBox(mdia);
        writer.EndBox(trak);
    }

    private static void WriteMovieHeader(IsoBmffWriter writer, long duration)
    {
        var box = writer.BeginBox("mvhd");
        WriteFullBoxHeader(writer, 1, 0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt32(MediaTime.DefaultTrackTimescale);
        writer.WriteUInt64(checked((ulong)duration));
        writer.WriteFixed16_16(1.0);
        writer.WriteUInt16(0x0100);
        writer.WriteUInt16(0);
        writer.WriteZeros(8);
        WriteIdentityMatrix(writer);
        writer.WriteZeros(24);
        writer.WriteUInt32(3);
        writer.EndBox(box);
    }

    private static void WriteTrackHeader(IsoBmffWriter writer, int trackId, long duration, bool audio, int width, int height)
    {
        var box = writer.BeginBox("tkhd");
        WriteFullBoxHeader(writer, 1, 7);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt32((uint)trackId);
        writer.WriteUInt32(0);
        writer.WriteUInt64(checked((ulong)duration));
        writer.WriteZeros(8);
        writer.WriteInt16(0);
        writer.WriteInt16(0);
        writer.WriteUInt16(audio ? (ushort)0x0100 : (ushort)0);
        writer.WriteUInt16(0);
        WriteIdentityMatrix(writer);
        writer.WriteUInt32((uint)width << 16);
        writer.WriteUInt32((uint)height << 16);
        writer.EndBox(box);
    }

    private static void WriteMediaHeader(IsoBmffWriter writer, long duration)
    {
        var box = writer.BeginBox("mdhd");
        WriteFullBoxHeader(writer, 1, 0);
        writer.WriteUInt64(0);
        writer.WriteUInt64(0);
        writer.WriteUInt32(MediaTime.DefaultTrackTimescale);
        writer.WriteUInt64(checked((ulong)duration));
        writer.WriteUInt16(0x55c4);
        writer.WriteUInt16(0);
        writer.EndBox(box);
    }

    private static void WriteHandler(IsoBmffWriter writer, string handlerType, string name)
    {
        var box = writer.BeginBox("hdlr");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32(0);
        writer.WriteFourCc(handlerType);
        writer.WriteZeros(12);
        var nameBytes = Encoding.ASCII.GetBytes(name);
        writer.WriteBytes(nameBytes);
        writer.WriteUInt8(0);
        writer.EndBox(box);
    }

    private static void WriteDataInformation(IsoBmffWriter writer)
    {
        var dinf = writer.BeginBox("dinf");
        var dref = writer.BeginBox("dref");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32(1);
        var url = writer.BeginBox("url ");
        WriteFullBoxHeader(writer, 0, 1);
        writer.EndBox(url);
        writer.EndBox(dref);
        writer.EndBox(dinf);
    }

    private static void WriteSampleTable(
        IsoBmffWriter writer,
        bool video,
        IList<Mp4Sample> samples,
        VideoCodecConfiguration? videoConfiguration,
        AacCodecConfiguration? audioConfiguration,
        long chunkOffsetAdjustment)
    {
        var stbl = writer.BeginBox("stbl");
        var stsd = writer.BeginBox("stsd");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32(1);
        if (video)
        {
            WriteVideoSampleEntry(writer, videoConfiguration!);
        }
        else
        {
            WriteAudioSampleEntry(writer, audioConfiguration!);
        }

        writer.EndBox(stsd);
        WriteTimeToSample(writer, samples);
        WriteSampleToChunk(writer, samples.Count);
        WriteSampleSizes(writer, samples);
        WriteChunkOffsets(writer, samples, chunkOffsetAdjustment);
        if (video)
        {
            WriteSyncSamples(writer, samples);
            WriteCompositionOffsets(writer, samples);
        }

        writer.EndBox(stbl);
    }

    private static void WriteVideoSampleEntry(IsoBmffWriter writer, VideoCodecConfiguration configuration)
    {
        var entry = writer.BeginBox(configuration.Codec == VideoCodec.H264 ? "avc1" : "hvc1");
        writer.WriteZeros(6);
        writer.WriteUInt16(1);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteZeros(12);
        writer.WriteUInt16((ushort)configuration.Width);
        writer.WriteUInt16((ushort)configuration.Height);
        writer.WriteUInt32(0x00480000);
        writer.WriteUInt32(0x00480000);
        writer.WriteUInt32(0);
        writer.WriteUInt16(1);
        writer.WriteZeros(32);
        writer.WriteUInt16(0x0018);
        writer.WriteInt16(-1);
        if (configuration.Codec == VideoCodec.H264)
        {
            WriteAvcConfiguration(writer, configuration);
        }
        else
        {
            WriteHevcConfiguration(writer, configuration);
        }

        writer.EndBox(entry);
    }

    private static void WriteAudioSampleEntry(IsoBmffWriter writer, AacCodecConfiguration configuration)
    {
        var entry = writer.BeginBox("mp4a");
        writer.WriteZeros(6);
        writer.WriteUInt16(1);
        writer.WriteZeros(8);
        writer.WriteUInt16((ushort)ChannelCount(configuration.ChannelConfiguration));
        writer.WriteUInt16(16);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt32((uint)configuration.SampleRate << 16);
        WriteEsds(writer, configuration);
        writer.EndBox(entry);
    }

    private static void WriteAvcConfiguration(IsoBmffWriter writer, VideoCodecConfiguration configuration)
    {
        var sps = SingleParameterSet(configuration.SpsBytes, "SPS");
        var pps = SingleParameterSet(configuration.PpsBytes, "PPS");
        var box = writer.BeginBox("avcC");
        writer.WriteUInt8(1);
        writer.WriteUInt8(sps.Length > 1 ? sps[1] : (byte)66);
        writer.WriteUInt8(sps.Length > 2 ? sps[2] : (byte)0);
        writer.WriteUInt8(sps.Length > 3 ? sps[3] : (byte)30);
        writer.WriteUInt8((byte)(0xfc | (configuration.NalLengthSize - 1)));
        writer.WriteUInt8(0xe1);
        writer.WriteUInt16((ushort)sps.Length);
        writer.WriteBytes(sps);
        writer.WriteUInt8(1);
        writer.WriteUInt16((ushort)pps.Length);
        writer.WriteBytes(pps);
        writer.EndBox(box);
    }

    private static void WriteHevcConfiguration(IsoBmffWriter writer, VideoCodecConfiguration configuration)
    {
        var vps = SingleParameterSet(configuration.VpsBytes, "VPS");
        var sps = SingleParameterSet(configuration.SpsBytes, "SPS");
        var pps = SingleParameterSet(configuration.PpsBytes, "PPS");
        var box = writer.BeginBox("hvcC");
        writer.WriteUInt8(1);
        writer.WriteUInt8(1);
        writer.WriteUInt32(0);
        writer.WriteZeros(6);
        writer.WriteUInt8(120);
        writer.WriteUInt16(0xf000);
        writer.WriteUInt8(0xfc);
        writer.WriteUInt8(0xfd);
        writer.WriteUInt8(0xf8);
        writer.WriteUInt8(0xf8);
        writer.WriteUInt16(0);
        writer.WriteUInt8((byte)(configuration.NalLengthSize - 1));
        writer.WriteUInt8(3);
        WriteHevcArray(writer, 32, vps);
        WriteHevcArray(writer, 33, sps);
        WriteHevcArray(writer, 34, pps);
        writer.EndBox(box);
    }

    private static void WriteHevcArray(IsoBmffWriter writer, int nalType, byte[] nal)
    {
        writer.WriteUInt8((byte)(0x80 | nalType));
        writer.WriteUInt16(1);
        writer.WriteUInt16((ushort)nal.Length);
        writer.WriteBytes(nal);
    }

    private static void WriteEsds(IsoBmffWriter writer, AacCodecConfiguration configuration)
    {
        var decSpecific = Descriptor(0x05, configuration.AudioSpecificConfigBytes);
        var decoderConfig = BuildBytes(w =>
        {
            w.WriteUInt8(0x40);
            w.WriteUInt8(0x15);
            w.WriteUInt8(0);
            w.WriteUInt8(0);
            w.WriteUInt8(0);
            w.WriteUInt32(0);
            w.WriteUInt32(0);
            w.WriteBytes(decSpecific);
        });
        var esDescriptor = BuildBytes(w =>
        {
            w.WriteUInt16(1);
            w.WriteUInt8(0);
            w.WriteBytes(Descriptor(0x04, decoderConfig));
            w.WriteBytes(Descriptor(0x06, new byte[] { 2 }));
        });

        var box = writer.BeginBox("esds");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteBytes(Descriptor(0x03, esDescriptor));
        writer.EndBox(box);
    }

    private static void WriteTimeToSample(IsoBmffWriter writer, IList<Mp4Sample> samples)
    {
        var box = writer.BeginBox("stts");
        WriteFullBoxHeader(writer, 0, 0);
        var entries = new List<RunLengthEntry>();
        foreach (var sample in samples)
        {
            if (sample.Duration > uint.MaxValue) throw new Mp4FormatException("A sample duration exceeds the stts range.");
            AddRun(entries, sample.Duration);
        }

        writer.WriteUInt32((uint)entries.Count);
        foreach (var entry in entries)
        {
            writer.WriteUInt32((uint)entry.Count);
            writer.WriteUInt32((uint)entry.Value);
        }

        writer.EndBox(box);
    }

    private static void WriteSampleToChunk(IsoBmffWriter writer, int sampleCount)
    {
        var box = writer.BeginBox("stsc");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32(sampleCount == 0 ? 0 : 1);
        if (sampleCount != 0)
        {
            writer.WriteUInt32(1);
            writer.WriteUInt32(1);
            writer.WriteUInt32(1);
        }

        writer.EndBox(box);
    }

    private static void WriteSampleSizes(IsoBmffWriter writer, IList<Mp4Sample> samples)
    {
        var box = writer.BeginBox("stsz");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)samples.Count);
        foreach (var sample in samples)
        {
            if (sample.Size < 0 || sample.Size > uint.MaxValue) throw new Mp4FormatException("A sample size is outside the stsz range.");
            writer.WriteUInt32((uint)sample.Size);
        }

        writer.EndBox(box);
    }

    private static void WriteChunkOffsets(IsoBmffWriter writer, IList<Mp4Sample> samples, long adjustment)
    {
        var adjustedOffsets = GetOffsets(samples, adjustment);
        var useCo64 = SampleTableDecisions.RequiresCo64(adjustedOffsets);
        var box = writer.BeginBox(useCo64 ? "co64" : "stco");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32((uint)samples.Count);
        foreach (var sample in samples)
        {
            var adjustedOffset = FastStartLayout.AdjustOffset(sample.Offset, adjustment);
            if (useCo64) writer.WriteUInt64(checked((ulong)adjustedOffset));
            else writer.WriteUInt32(adjustedOffset);
        }

        writer.EndBox(box);
    }

    private static IEnumerable<long> GetOffsets(IList<Mp4Sample> samples, long adjustment)
    {
        foreach (var sample in samples) yield return FastStartLayout.AdjustOffset(sample.Offset, adjustment);
    }

    private static void WriteSyncSamples(IsoBmffWriter writer, IList<Mp4Sample> samples)
    {
        var keyFrames = new List<int>();
        for (var i = 0; i < samples.Count; i++) if (samples[i].IsKeyFrame) keyFrames.Add(i + 1);
        if (keyFrames.Count == samples.Count) return;
        var box = writer.BeginBox("stss");
        WriteFullBoxHeader(writer, 0, 0);
        writer.WriteUInt32((uint)keyFrames.Count);
        foreach (var keyFrame in keyFrames) writer.WriteUInt32((uint)keyFrame);
        writer.EndBox(box);
    }

    private static void WriteCompositionOffsets(IsoBmffWriter writer, IList<Mp4Sample> samples)
    {
        var entries = new List<CompositionEntry>();
        var hasOffset = false;
        foreach (var sample in samples)
        {
            var offset = checked(sample.Pts - sample.Dts);
            if (offset != 0) hasOffset = true;
            if (offset < int.MinValue || offset > int.MaxValue) throw new Mp4FormatException("A composition offset exceeds ctts range.");
            if (entries.Count != 0 && entries[entries.Count - 1].Offset == offset)
            {
                entries[entries.Count - 1] = new CompositionEntry(entries[entries.Count - 1].Count + 1, offset);
            }
            else
            {
                entries.Add(new CompositionEntry(1, offset));
            }
        }

        if (!hasOffset) return;
        var box = writer.BeginBox("ctts");
        WriteFullBoxHeader(writer, 1, 0);
        writer.WriteUInt32((uint)entries.Count);
        foreach (var entry in entries)
        {
            writer.WriteUInt32((uint)entry.Count);
            writer.WriteInt32((int)entry.Offset);
        }

        writer.EndBox(box);
    }

    private static void AddRun(IList<RunLengthEntry> entries, long value)
    {
        if (entries.Count != 0 && entries[entries.Count - 1].Value == value)
        {
            var previous = entries[entries.Count - 1];
            entries[entries.Count - 1] = new RunLengthEntry(previous.Count + 1, value);
        }
        else
        {
            entries.Add(new RunLengthEntry(1, value));
        }
    }

    private static void WriteFileTypeBox(IsoBmffWriter writer)
    {
        var box = writer.BeginBox("ftyp");
        writer.WriteFourCc("isom");
        writer.WriteUInt32(0x00000200);
        writer.WriteFourCc("isom");
        writer.WriteFourCc("iso6");
        writer.WriteFourCc("mp41");
        writer.EndBox(box);
    }

    private static void WriteFullBoxHeader(IsoBmffWriter writer, byte version, uint flags)
    {
        writer.WriteUInt8(version);
        writer.WriteUInt8((byte)(flags >> 16));
        writer.WriteUInt8((byte)(flags >> 8));
        writer.WriteUInt8((byte)flags);
    }

    private static void WriteIdentityMatrix(IsoBmffWriter writer)
    {
        writer.WriteInt32(0x00010000);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0x00010000);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0x40000000);
    }

    private static byte[] SingleParameterSet(byte[] value, string name)
    {
        var sets = NalUnits.Normalize(value);
        if (sets.Count != 1 || sets[0].Count == 0)
        {
            throw new Mp4FormatException("The configured " + name + " must contain exactly one NAL unit.");
        }

        if (sets[0].Count > ushort.MaxValue) throw new Mp4FormatException("The configured " + name + " is too large.");
        return sets[0].ToArray();
    }

    private static void WriteNalLength(IsoBmffWriter writer, int length, int lengthSize)
    {
        switch (lengthSize)
        {
            case 1: writer.WriteUInt8((byte)length); break;
            case 2: writer.WriteUInt16((ushort)length); break;
            case 3:
                writer.WriteUInt8((byte)(length >> 16));
                writer.WriteUInt8((byte)(length >> 8));
                writer.WriteUInt8((byte)length);
                break;
            case 4: writer.WriteUInt32((uint)length); break;
            default: throw new ArgumentOutOfRangeException(nameof(lengthSize));
        }
    }

    private static long MaxLengthForNal(int lengthSize)
    {
        return lengthSize == 4 ? uint.MaxValue : (1L << (lengthSize * 8)) - 1;
    }

    private static int ChannelCount(int channelConfiguration)
    {
        return channelConfiguration == 7 ? 8 : channelConfiguration;
    }

    private static byte[] Descriptor(byte tag, byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        using (var stream = new MemoryStream())
        {
            stream.WriteByte(tag);
            WriteDescriptorLength(stream, payload.Length);
            stream.Write(payload, 0, payload.Length);
            return stream.ToArray();
        }
    }

    private static void WriteDescriptorLength(Stream stream, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = new byte[4];
        var count = 0;
        do
        {
            bytes[count++] = (byte)(length & 0x7f);
            length >>= 7;
        } while (length != 0 && count < bytes.Length);
        if (length != 0) throw new Mp4FormatException("An MPEG-4 descriptor is too large.");
        for (var i = count - 1; i >= 0; i--)
        {
            var value = bytes[i];
            if (i != 0) value |= 0x80;
            stream.WriteByte(value);
        }
    }

    private static byte[] BuildBytes(Action<IsoBmffWriter> action)
    {
        using (var stream = new MemoryStream())
        {
            action(new IsoBmffWriter(stream));
            return stream.ToArray();
        }
    }

    private static long GetTrackDuration(IList<Mp4Sample> samples)
    {
        var duration = 0L;
        foreach (var sample in samples)
        {
            duration = Math.Max(duration, checked(sample.Dts + sample.Duration));
        }

        return duration;
    }

    private static void ValidateTimedSample(long dts, long duration, long? previousDts, string trackName)
    {
        if (dts < 0) throw new Mp4TimestampException(trackName + " DTS must not be negative.");
        if (duration <= 0) throw new Mp4TimestampException(trackName + " duration must be positive.");
        if (previousDts.HasValue && dts < previousDts.Value)
        {
            throw new Mp4TimestampException(trackName + " DTS must not decrease from one submitted sample to the next.");
        }
    }

    private void EnsureWritable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Mp4Writer));
        if (_finalized) throw new InvalidOperationException("The MP4 writer has already been finalized.");
    }

    private sealed class PendingVideoAccessUnit
    {
        public PendingVideoAccessUnit(long pts, long dts, long duration, bool isKeyFrame, IList<NalUnitRange> nals)
        {
            Pts = pts;
            Dts = dts;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
            Nals = new List<NalUnitRange>(nals);
        }

        public long Pts { get; }
        public long Dts { get; }
        public long Duration { get; }
        public bool IsKeyFrame { get; }
        public List<NalUnitRange> Nals { get; }
    }

    private sealed class Mp4Sample
    {
        public Mp4Sample(long offset, long size, long pts, long dts, long duration, bool isKeyFrame)
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

    private sealed class FragmentSample
    {
        public FragmentSample(
            bool video,
            FragmentPayloadSource payloadSource,
            long pts,
            long dts,
            long duration,
            bool isKeyFrame)
        {
            Video = video;
            PayloadSource = payloadSource ?? throw new ArgumentNullException(nameof(payloadSource));
            Pts = pts;
            Dts = dts;
            Duration = duration;
            IsKeyFrame = isKeyFrame;
        }

        public bool Video { get; }
        public FragmentPayloadSource PayloadSource { get; }
        public int EncodedSize => PayloadSource.EncodedSize;
        public long Pts { get; }
        public long Dts { get; }
        public long Duration { get; }
        public bool IsKeyFrame { get; }
    }

    private sealed class RunLengthEntry
    {
        public RunLengthEntry(int count, long value) { Count = count; Value = value; }
        public int Count { get; }
        public long Value { get; }
    }

    private sealed class CompositionEntry
    {
        public CompositionEntry(int count, long offset) { Count = count; Offset = offset; }
        public int Count { get; }
        public long Offset { get; }
    }
}

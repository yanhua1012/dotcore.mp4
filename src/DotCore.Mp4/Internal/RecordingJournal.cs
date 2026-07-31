using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4;

internal enum RecordingSampleKind : byte
{
    Video = 1,
    Audio = 2
}

internal sealed class RecordingJournalHeader
{
    public RecordingJournalHeader(Mp4WriteMode mode, Guid captureId, byte[] targetIdentity)
    {
        Mode = mode;
        CaptureId = captureId;
        TargetIdentity = targetIdentity;
    }

    public Mp4WriteMode Mode { get; }

    public Guid CaptureId { get; }

    public byte[] TargetIdentity { get; }
}

internal sealed class RecordingJournalSample
{
    public RecordingJournalSample(
        RecordingSampleKind kind,
        long offset,
        int size,
        long presentationTimestamp,
        long decodeTimestamp,
        long duration,
        bool isKeyFrame,
        long boundary)
    {
        Kind = kind;
        Offset = offset;
        Size = size;
        PresentationTimestamp = presentationTimestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
        IsKeyFrame = isKeyFrame;
        Boundary = boundary;
    }

    public RecordingSampleKind Kind { get; }

    public long Offset { get; }

    public int Size { get; }

    public long PresentationTimestamp { get; }

    public long DecodeTimestamp { get; }

    public long Duration { get; }

    public bool IsKeyFrame { get; }

    public long Boundary { get; }
}

internal sealed class RecordingJournalSnapshot
{
    public RecordingJournalSnapshot(RecordingJournalHeader header)
    {
        Header = header;
        Samples = new List<RecordingJournalSample>();
        Warnings = new List<string>();
    }

    public RecordingJournalHeader Header { get; }

    public VideoCodecConfiguration? VideoConfiguration { get; set; }

    public AacCodecConfiguration? AudioConfiguration { get; set; }

    public IList<RecordingJournalSample> Samples { get; }

    public IList<string> Warnings { get; }

    public bool IsTrusted { get; set; } = true;

    public byte[]? CompletedTargetHash { get; set; }
}

internal static class RecordingJournal
{
    private const byte Version = 1;
    private const int TargetIdentityLength = 32;
    private const int MaximumJournalBytes = 64 * 1024 * 1024;
    private const int MaximumRecordBytes = 4 * 1024 * 1024;
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly byte[] Magic = { (byte)'D', (byte)'C', (byte)'R', (byte)'J', (byte)'N', (byte)'L', (byte)'0', (byte)'1' };

    private enum RecordType : byte
    {
        VideoConfiguration = 1,
        AudioConfiguration = 2,
        Sample = 3,
        Completed = 4
    }

    public static void WriteHeader(Stream stream, RecordingJournalHeader header)
    {
        WriteBytes(stream, BuildHeader(header));
    }

    public static Task WriteHeaderAsync(
        Stream stream,
        RecordingJournalHeader header,
        CancellationToken cancellationToken)
    {
        return stream.WriteAsync(BuildHeader(header), 0, HeaderLength, cancellationToken);
    }

    public static void AppendVideoConfiguration(Stream stream, VideoCodecConfiguration configuration)
    {
        Append(stream, RecordType.VideoConfiguration, BuildVideoConfigurationPayload(configuration));
    }

    public static Task AppendVideoConfigurationAsync(
        Stream stream,
        VideoCodecConfiguration configuration,
        CancellationToken cancellationToken)
    {
        return AppendAsync(stream, RecordType.VideoConfiguration, BuildVideoConfigurationPayload(configuration), cancellationToken);
    }

    public static void AppendAudioConfiguration(Stream stream, AacCodecConfiguration configuration)
    {
        Append(stream, RecordType.AudioConfiguration, BuildAudioConfigurationPayload(configuration));
    }

    public static Task AppendAudioConfigurationAsync(
        Stream stream,
        AacCodecConfiguration configuration,
        CancellationToken cancellationToken)
    {
        return AppendAsync(stream, RecordType.AudioConfiguration, BuildAudioConfigurationPayload(configuration), cancellationToken);
    }

    public static void AppendSample(Stream stream, RecordingJournalSample sample)
    {
        Append(stream, RecordType.Sample, BuildSamplePayload(sample));
    }

    public static Task AppendSampleAsync(
        Stream stream,
        RecordingJournalSample sample,
        CancellationToken cancellationToken)
    {
        return AppendAsync(stream, RecordType.Sample, BuildSamplePayload(sample), cancellationToken);
    }

    public static void AppendCompleted(Stream stream, byte[] targetHash)
    {
        if (targetHash == null) throw new ArgumentNullException(nameof(targetHash));
        if (targetHash.Length != TargetIdentityLength) throw new ArgumentException("A SHA-256 digest is required.", nameof(targetHash));
        Append(stream, RecordType.Completed, targetHash);
    }

    public static Task AppendCompletedAsync(Stream stream, byte[] targetHash, CancellationToken cancellationToken)
    {
        if (targetHash == null) throw new ArgumentNullException(nameof(targetHash));
        if (targetHash.Length != TargetIdentityLength) throw new ArgumentException("A SHA-256 digest is required.", nameof(targetHash));
        return AppendAsync(stream, RecordType.Completed, targetHash, cancellationToken);
    }

    public static RecordingJournalSnapshot Read(Stream stream, byte[] expectedTargetIdentity)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (expectedTargetIdentity == null) throw new ArgumentNullException(nameof(expectedTargetIdentity));
        if (stream.Length < HeaderLength || stream.Length > MaximumJournalBytes)
        {
            throw new Mp4FormatException("The recording journal length is invalid.");
        }

        stream.Position = 0;
        var data = new byte[checked((int)stream.Length)];
        ReadExactly(stream, data, 0, data.Length);
        return ReadData(data, expectedTargetIdentity);
    }

    public static async Task<RecordingJournalSnapshot> ReadAsync(
        Stream stream,
        byte[] expectedTargetIdentity,
        CancellationToken cancellationToken)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (expectedTargetIdentity == null) throw new ArgumentNullException(nameof(expectedTargetIdentity));
        if (stream.Length < HeaderLength || stream.Length > MaximumJournalBytes)
        {
            throw new Mp4FormatException("The recording journal length is invalid.");
        }

        stream.Position = 0;
        var data = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream
                .ReadAsync(data, offset, data.Length - offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new Mp4FormatException("The recording journal ended unexpectedly.");
            offset += read;
        }

        return ReadData(data, expectedTargetIdentity);
    }

    private static int HeaderLength => Magic.Length + 1 + 1 + TargetIdentityLength + 16;

    private static byte[] BuildHeader(RecordingJournalHeader header)
    {
        if (header == null) throw new ArgumentNullException(nameof(header));
        if (header.TargetIdentity == null || header.TargetIdentity.Length != TargetIdentityLength)
        {
            throw new ArgumentException("A SHA-256 target identity is required.", nameof(header));
        }

        if (header.Mode != Mp4WriteMode.Progressive &&
            header.Mode != Mp4WriteMode.FastStart &&
            header.Mode != Mp4WriteMode.Fragmented)
        {
            throw new ArgumentOutOfRangeException(nameof(header));
        }

        var buffer = new byte[HeaderLength];
        Buffer.BlockCopy(Magic, 0, buffer, 0, Magic.Length);
        buffer[Magic.Length] = Version;
        buffer[Magic.Length + 1] = (byte)header.Mode;
        Buffer.BlockCopy(header.TargetIdentity, 0, buffer, Magic.Length + 2, TargetIdentityLength);
        var captureId = header.CaptureId.ToByteArray();
        Buffer.BlockCopy(captureId, 0, buffer, Magic.Length + 2 + TargetIdentityLength, captureId.Length);
        return buffer;
    }

    private static byte[] BuildVideoConfigurationPayload(VideoCodecConfiguration configuration)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        var vps = configuration.VpsBytes;
        var sps = configuration.SpsBytes;
        var pps = configuration.PpsBytes;
        if (vps.Length > ushort.MaxValue || sps.Length > ushort.MaxValue || pps.Length > ushort.MaxValue)
        {
            throw new Mp4FormatException("A video codec configuration is too large for the recording journal.");
        }

        var payload = new byte[checked(12 + vps.Length + sps.Length + pps.Length)];
        payload[0] = (byte)configuration.Codec;
        payload[1] = checked((byte)configuration.NalLengthSize);
        WriteU16(payload, 2, checked((ushort)configuration.Width));
        WriteU16(payload, 4, checked((ushort)configuration.Height));
        WriteU16(payload, 6, checked((ushort)vps.Length));
        WriteU16(payload, 8, checked((ushort)sps.Length));
        WriteU16(payload, 10, checked((ushort)pps.Length));
        var offset = 12;
        Buffer.BlockCopy(vps, 0, payload, offset, vps.Length);
        offset += vps.Length;
        Buffer.BlockCopy(sps, 0, payload, offset, sps.Length);
        offset += sps.Length;
        Buffer.BlockCopy(pps, 0, payload, offset, pps.Length);
        return payload;
    }

    private static byte[] BuildAudioConfigurationPayload(AacCodecConfiguration configuration)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        var asc = configuration.AudioSpecificConfigBytes;
        if (asc.Length > ushort.MaxValue)
        {
            throw new Mp4FormatException("An AAC codec configuration is too large for the recording journal.");
        }

        var payload = new byte[checked(7 + asc.Length)];
        WriteU16(payload, 0, checked((ushort)asc.Length));
        WriteU32(payload, 2, checked((uint)configuration.SampleRate));
        payload[6] = checked((byte)configuration.ChannelConfiguration);
        Buffer.BlockCopy(asc, 0, payload, 7, asc.Length);
        return payload;
    }

    private static byte[] BuildSamplePayload(RecordingJournalSample sample)
    {
        if (sample == null) throw new ArgumentNullException(nameof(sample));
        var payload = new byte[46];
        payload[0] = (byte)sample.Kind;
        payload[1] = sample.IsKeyFrame ? (byte)1 : (byte)0;
        WriteI64(payload, 2, sample.Offset);
        WriteU32(payload, 10, checked((uint)sample.Size));
        WriteI64(payload, 14, sample.PresentationTimestamp);
        WriteI64(payload, 22, sample.DecodeTimestamp);
        WriteI64(payload, 30, sample.Duration);
        WriteI64(payload, 38, sample.Boundary);
        return payload;
    }

    private static void Append(Stream stream, RecordType type, byte[] payload)
    {
        WriteBytes(stream, BuildRecord(type, payload));
    }

    private static Task AppendAsync(
        Stream stream,
        RecordType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var record = BuildRecord(type, payload);
        return stream.WriteAsync(record, 0, record.Length, cancellationToken);
    }

    private static byte[] BuildRecord(RecordType type, byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length > MaximumRecordBytes) throw new Mp4FormatException("A recording journal record is too large.");
        var buffer = new byte[checked(9 + payload.Length)];
        buffer[0] = (byte)type;
        WriteU32(buffer, 1, checked((uint)payload.Length));
        Buffer.BlockCopy(payload, 0, buffer, 5, payload.Length);
        WriteU32(buffer, 5 + payload.Length, Crc32(buffer, 0, 5 + payload.Length));
        return buffer;
    }

    private static RecordingJournalSnapshot ReadData(byte[] data, byte[] expectedTargetIdentity)
    {
        var offset = 0;
        for (var index = 0; index < Magic.Length; index++)
        {
            if (data[offset++] != Magic[index]) throw new Mp4FormatException("The recording journal magic is invalid.");
        }

        if (data[offset++] != Version) throw new Mp4FormatException("The recording journal version is unsupported.");
        var mode = (Mp4WriteMode)data[offset++];
        if (mode != Mp4WriteMode.Progressive && mode != Mp4WriteMode.FastStart && mode != Mp4WriteMode.Fragmented)
        {
            throw new Mp4FormatException("The recording journal layout is invalid.");
        }

        var identity = Slice(data, offset, TargetIdentityLength);
        offset += TargetIdentityLength;
        if (!FixedTimeEquals(identity, expectedTargetIdentity))
        {
            throw new Mp4FormatException("The recording journal does not belong to the requested target.");
        }

        var captureBytes = Slice(data, offset, 16);
        offset += 16;
        var snapshot = new RecordingJournalSnapshot(
            new RecordingJournalHeader(mode, new Guid(captureBytes), identity));

        while (offset < data.Length)
        {
            if (data.Length - offset < 5)
            {
                snapshot.Warnings.Add("Ignored an incomplete trailing recording journal record.");
                break;
            }

            var type = (RecordType)data[offset];
            var payloadLength = ReadU32(data, offset + 1);
            if (payloadLength > MaximumRecordBytes || payloadLength > data.Length - offset - 9)
            {
                snapshot.Warnings.Add("Ignored an incomplete trailing recording journal record.");
                break;
            }

            var recordLength = checked((int)payloadLength + 9);
            var checksum = ReadU32(data, offset + 5 + (int)payloadLength);
            if (checksum != Crc32(data, offset, 5 + (int)payloadLength))
            {
                snapshot.IsTrusted = false;
                snapshot.Warnings.Add("The recording journal checksum is invalid.");
                break;
            }

            var payload = Slice(data, offset + 5, checked((int)payloadLength));
            try
            {
                ApplyRecord(snapshot, type, payload);
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is ArgumentOutOfRangeException ||
                error is Mp4FormatException ||
                error is OverflowException)
            {
                snapshot.IsTrusted = false;
                snapshot.Warnings.Add("The recording journal contains an invalid record.");
                break;
            }

            offset += recordLength;
        }

        ValidateSnapshot(snapshot);
        return snapshot;
    }

    private static void ApplyRecord(RecordingJournalSnapshot snapshot, RecordType type, byte[] payload)
    {
        if (snapshot.CompletedTargetHash != null)
        {
            throw new Mp4FormatException("The recording journal contains records after its completion marker.");
        }

        switch (type)
        {
            case RecordType.VideoConfiguration:
                if (snapshot.VideoConfiguration != null)
                {
                    throw new Mp4FormatException("The recording journal video configuration order is invalid.");
                }

                snapshot.VideoConfiguration = ReadVideoConfiguration(payload);
                return;
            case RecordType.AudioConfiguration:
                if (snapshot.AudioConfiguration != null)
                {
                    throw new Mp4FormatException("The recording journal audio configuration order is invalid.");
                }

                snapshot.AudioConfiguration = ReadAudioConfiguration(payload);
                return;
            case RecordType.Sample:
                snapshot.Samples.Add(ReadSample(payload));
                if (snapshot.Samples.Count > Mp4Reader.MaximumSampleCount)
                {
                    throw new Mp4FormatException("The recording journal sample count exceeds the supported limit.");
                }

                return;
            case RecordType.Completed:
                if (snapshot.CompletedTargetHash != null || payload.Length != TargetIdentityLength)
                {
                    throw new Mp4FormatException("The recording journal completion marker is invalid.");
                }

                snapshot.CompletedTargetHash = payload;
                return;
            default:
                throw new Mp4FormatException("The recording journal record type is unsupported.");
        }
    }

    private static VideoCodecConfiguration ReadVideoConfiguration(byte[] payload)
    {
        if (payload.Length < 12 || payload.Length > MaximumConfigurationBytes)
        {
            throw new Mp4FormatException("The recording journal video configuration is invalid.");
        }

        var codec = (VideoCodec)payload[0];
        var nalLengthSize = payload[1];
        var width = ReadU16(payload, 2);
        var height = ReadU16(payload, 4);
        var vpsLength = ReadU16(payload, 6);
        var spsLength = ReadU16(payload, 8);
        var ppsLength = ReadU16(payload, 10);
        var expected = checked(12 + vpsLength + spsLength + ppsLength);
        if (expected != payload.Length) throw new Mp4FormatException("The recording journal video configuration length is invalid.");
        var offset = 12;
        var vps = Slice(payload, offset, vpsLength);
        offset += vpsLength;
        var sps = Slice(payload, offset, spsLength);
        offset += spsLength;
        var pps = Slice(payload, offset, ppsLength);
        if (codec == VideoCodec.H264 && vpsLength == 0)
        {
            return new VideoCodecConfiguration(codec, sps, pps, nalLengthSize, width, height);
        }

        if (codec == VideoCodec.H265)
        {
            return new VideoCodecConfiguration(codec, vps, sps, pps, nalLengthSize, width, height);
        }

        throw new Mp4FormatException("The recording journal video codec is invalid.");
    }

    private static AacCodecConfiguration ReadAudioConfiguration(byte[] payload)
    {
        if (payload.Length < 7 || payload.Length > MaximumConfigurationBytes)
        {
            throw new Mp4FormatException("The recording journal AAC configuration is invalid.");
        }

        var length = ReadU16(payload, 0);
        if (payload.Length != 7 + length) throw new Mp4FormatException("The recording journal AAC configuration length is invalid.");
        var sampleRate = ReadU32(payload, 2);
        var channels = payload[6];
        if (sampleRate > int.MaxValue) throw new Mp4FormatException("The recording journal AAC sample rate is invalid.");
        return new AacCodecConfiguration(Slice(payload, 7, length), (int)sampleRate, channels);
    }

    private static RecordingJournalSample ReadSample(byte[] payload)
    {
        if (payload.Length != 46)
        {
            throw new Mp4FormatException("The recording journal sample record length is invalid.");
        }

        var kind = (RecordingSampleKind)payload[0];
        if (kind != RecordingSampleKind.Video && kind != RecordingSampleKind.Audio)
        {
            throw new Mp4FormatException("The recording journal sample type is invalid.");
        }

        if (payload[1] > 1) throw new Mp4FormatException("The recording journal key-frame flag is invalid.");
        var size = ReadU32(payload, 10);
        if (size == 0 || size > int.MaxValue) throw new Mp4FormatException("The recording journal sample size is invalid.");
        return new RecordingJournalSample(
            kind,
            ReadI64(payload, 2),
            (int)size,
            ReadI64(payload, 14),
            ReadI64(payload, 22),
            ReadI64(payload, 30),
            payload[1] != 0,
            ReadI64(payload, 38));
    }

    private static void ValidateSnapshot(RecordingJournalSnapshot snapshot)
    {
        var expectedOffset = RecordingCapture.PayloadOffset;
        long? lastVideoDts = null;
        long? lastAudioDts = null;
        foreach (var sample in snapshot.Samples)
        {
            if ((sample.Kind == RecordingSampleKind.Video && snapshot.VideoConfiguration == null) ||
                (sample.Kind == RecordingSampleKind.Audio && snapshot.AudioConfiguration == null))
            {
                snapshot.IsTrusted = false;
                snapshot.Warnings.Add("The recording journal references an unconfigured track.");
                return;
            }

            long expectedBoundary;
            try
            {
                expectedBoundary = checked(sample.Offset + sample.Size);
            }
            catch (OverflowException)
            {
                snapshot.IsTrusted = false;
                snapshot.Warnings.Add("The recording journal sample boundary is invalid.");
                return;
            }

            if (sample.Offset != expectedOffset ||
                sample.Size <= 0 ||
                sample.Boundary != expectedBoundary ||
                sample.PresentationTimestamp < 0 ||
                sample.DecodeTimestamp < 0 ||
                sample.Duration <= 0)
            {
                snapshot.IsTrusted = false;
                snapshot.Warnings.Add("The recording journal sample boundary is invalid.");
                return;
            }

            var previous = sample.Kind == RecordingSampleKind.Video ? lastVideoDts : lastAudioDts;
            if (previous.HasValue && sample.DecodeTimestamp < previous.Value)
            {
                snapshot.IsTrusted = false;
                snapshot.Warnings.Add("The recording journal timestamps are not monotonic.");
                return;
            }

            if (sample.Kind == RecordingSampleKind.Video) lastVideoDts = sample.DecodeTimestamp;
            else lastAudioDts = sample.DecodeTimestamp;
            expectedOffset = sample.Boundary;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count != 0)
        {
            var read = stream.Read(buffer, offset, count);
            if (read == 0) throw new Mp4FormatException("The recording journal ended unexpectedly.");
            offset += read;
            count -= read;
        }
    }

    private static void WriteBytes(Stream stream, byte[] value)
    {
        stream.Write(value, 0, value.Length);
    }

    private static byte[] Slice(byte[] value, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > value.Length - length)
        {
            throw new Mp4FormatException("The recording journal contains an out-of-range field.");
        }

        var copy = new byte[length];
        Buffer.BlockCopy(value, offset, copy, 0, length);
        return copy;
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
        return difference == 0;
    }

    private static uint Crc32(byte[] value, int offset, int count)
    {
        uint crc = 0xffffffff;
        for (var index = 0; index < count; index++)
        {
            crc ^= value[offset + index];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U);
            }
        }

        return ~crc;
    }

    private static ushort ReadU16(byte[] value, int offset)
    {
        if (offset < 0 || offset > value.Length - 2) throw new Mp4FormatException("The recording journal ended unexpectedly.");
        return (ushort)((value[offset] << 8) | value[offset + 1]);
    }

    private static uint ReadU32(byte[] value, int offset)
    {
        if (offset < 0 || offset > value.Length - 4) throw new Mp4FormatException("The recording journal ended unexpectedly.");
        return ((uint)value[offset] << 24) |
               ((uint)value[offset + 1] << 16) |
               ((uint)value[offset + 2] << 8) |
               value[offset + 3];
    }

    private static long ReadI64(byte[] value, int offset)
    {
        if (offset < 0 || offset > value.Length - 8) throw new Mp4FormatException("The recording journal ended unexpectedly.");
        ulong result = 0;
        for (var index = 0; index < 8; index++) result = (result << 8) | value[offset + index];
        return unchecked((long)result);
    }

    private static void WriteU16(byte[] value, int offset, ushort number)
    {
        value[offset] = (byte)(number >> 8);
        value[offset + 1] = (byte)number;
    }

    private static void WriteU32(byte[] value, int offset, uint number)
    {
        value[offset] = (byte)(number >> 24);
        value[offset + 1] = (byte)(number >> 16);
        value[offset + 2] = (byte)(number >> 8);
        value[offset + 3] = (byte)number;
    }

    private static void WriteI64(byte[] value, int offset, long number)
    {
        var unsigned = unchecked((ulong)number);
        for (var index = 7; index >= 0; index--)
        {
            value[offset + index] = (byte)unsigned;
            unsigned >>= 8;
        }
    }
}

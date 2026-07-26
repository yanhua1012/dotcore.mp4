using System.Collections;
using System.Reflection;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// Movie Fragment Payload 來源與記憶體結構測試套件。
/// </summary>
public sealed class FragmentPayloadSourceTests
{
    [Fact]
    public void VideoFragmentSampleStoresRangesAndLogicalEncodedSize()
    {
        var configuration = VideoCodecConfiguration.CreateH264(
            new byte[] { 0x67, 0x64 },
            new byte[] { 0x68, 0xee },
            nalLengthSize: 1);
        var annexB = new byte[]
        {
            0, 0, 0, 1, 0x65, 0x11, 0x12,
            0, 0, 0, 1, 0x06, 0x22
        };
        var sample = TestMedia.Video(annexB, TimeSpan.Zero, TimeSpan.Zero);
        using var output = new MemoryStream();
        using var writer = CreateWriter(output, maximumBytes: 9);
        writer.SetVideoCodecConfiguration(configuration);
        writer.WriteVideoNalUnit(sample);
        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x41 },
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(40),
            false));

        var fragmentSample = GetFragmentSamples(writer).Cast<object>().Single();
        var payloadMember = FindPayloadSourceMember(fragmentSample.GetType());
        Assert.NotNull(payloadMember);
        Assert.NotEqual(typeof(byte[]), GetMemberType(payloadMember!));
        Assert.DoesNotContain(
            GetDataMembers(fragmentSample.GetType()),
            member => GetMemberType(member) == typeof(byte[]) &&
                      member.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));

        var encodedSize = ReadNamedInt(
            fragmentSample,
            "EncodedSize",
            "LogicalEncodedSize",
            "PayloadSize");
        Assert.Equal(7, encodedSize);

        var payloadSource = GetMemberValue(payloadMember!, fragmentSample);
        Assert.NotNull(payloadSource);
        var ranges = FindRangeItems(payloadSource!).ToArray();
        Assert.Equal(2, ranges.Length);
        Assert.All(ranges, range => Assert.True(
            range.GetType().IsValueType,
            "Video fragment payloads must retain NAL value ranges rather than payload arrays or per-NAL objects."));
        Assert.All(ranges, range => Assert.Same(sample.DataBytes, ReadBackingArray(range)));
        Assert.Equal(new[] { 4, 11 }, ranges.Select(ReadOffset));
        Assert.Equal(new[] { 3, 2 }, ranges.Select(ReadCount));
    }

    [Fact]
    public void AnnexBPhysicalStartCodesDoNotCountTowardFragmentLimit()
    {
        var configuration = VideoCodecConfiguration.CreateH264(
            new byte[] { 0x67, 0x64 },
            new byte[] { 0x68, 0xee },
            nalLengthSize: 1);
        var annexB = new byte[]
        {
            0, 0, 0, 1, 0x65, 0x11, 0x12,
            0, 0, 0, 1, 0x06, 0x22
        };

        using var exactOutput = new MemoryStream();
        using (var exactWriter = CreateWriter(exactOutput, maximumBytes: 7))
        {
            exactWriter.SetVideoCodecConfiguration(configuration);
            exactWriter.WriteVideoNalUnit(TestMedia.Video(annexB, TimeSpan.Zero, TimeSpan.Zero));
            Assert.Equal(7, ReadPrivateInt(exactWriter, "_fragmentBufferedBytes"));
            exactWriter.FinalizeFile();
        }

        using (var reader = new Mp4Reader(exactOutput))
        {
            Assert.Equal(
                new[] { "651112", "0622" },
                reader.ReadVideoNalUnits().Select(sample => Convert.ToHexString(sample.Data)));
        }

        using var overflowOutput = new MemoryStream();
        using var overflowWriter = CreateWriter(overflowOutput, maximumBytes: 6);
        overflowWriter.SetVideoCodecConfiguration(configuration);
        var error = Assert.Throws<InvalidOperationException>(() =>
            overflowWriter.WriteVideoNalUnit(TestMedia.Video(annexB, TimeSpan.Zero, TimeSpan.Zero)));
        Assert.Contains("buffer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ReadPrivateInt(overflowWriter, "_fragmentBufferedBytes"));
        Assert.Empty(GetFragmentSamples(overflowWriter));
        Assert.Null(GetPrivateField(overflowWriter, "_pendingVideo"));
        Assert.Empty(overflowOutput.ToArray());
    }

    [Fact]
    public void OverflowAfterAcceptedRangesDoesNotRetainRejectedState()
    {
        using var output = new MemoryStream();
        using var writer = CreateWriter(output, maximumBytes: 9);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x65, 0x01 },
            TimeSpan.Zero,
            TimeSpan.Zero));
        Assert.Equal(6, ReadPrivateInt(writer, "_fragmentBufferedBytes"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteVideoNalUnit(TestMedia.Video(
                new byte[] { 0x06, 0x02 },
                TimeSpan.Zero,
                TimeSpan.Zero)));
        Assert.Contains("buffer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, ReadPrivateInt(writer, "_fragmentBufferedBytes"));

        writer.FinalizeFile();
        using var reader = new Mp4Reader(output);
        var sample = reader.ReadVideoNalUnits().Single();
        Assert.Equal(new byte[] { 0x65, 0x01 }, sample.Data);
    }

    [Fact]
    public void LargeMultiNalGopAndAacRoundTripOnNonSeekableOutput()
    {
        var fixture = LargeFixture.Create();
        using var output = new CollectingNonSeekableStream();
        using (var writer = CreateWriter(output, maximumBytes: fixture.BufferedBytes + 64))
        {
            WriteFirstFragmentInputs(writer, fixture);
            WriteVideo(writer, fixture.NextKeyFrame, 80, true);
            writer.FinalizeFile();
        }

        Assert.Equal(
            new[] { "ftyp", "moov", "moof", "mdat", "moof", "mdat" },
            ReadTopLevelBoxes(output.Bytes).Select(box => box.Type));
        using var reader = new Mp4Reader(new MemoryStream(output.Bytes));
        Assert.Equal(
            fixture.AllVideoPayloads.Select(Convert.ToHexString),
            reader.ReadVideoNalUnits().Select(sample => Convert.ToHexString(sample.Data)));
        Assert.Equal(
            new[] { fixture.Audio },
            reader.ReadAudioSamples().Select(sample => sample.Data));
    }

    [Fact]
    public void SuccessfulFlushAndFinalizationClearPayloadReferencesAndAccounting()
    {
        var fixture = LargeFixture.Create();
        using var output = new MemoryStream();
        using var writer = CreateWriter(output, maximumBytes: fixture.BufferedBytes + 64);
        WriteFirstFragmentInputs(writer, fixture);

        WriteVideo(writer, fixture.NextKeyFrame, 80, true);

        Assert.Empty(GetFragmentSamples(writer));
        Assert.NotNull(GetPrivateField(writer, "_pendingVideo"));
        Assert.Equal(fixture.NextKeyFrame.Length + 4, ReadPrivateInt(writer, "_fragmentBufferedBytes"));
        Assert.Equal(2U, ReadPrivateUInt(writer, "_fragmentSequenceNumber"));

        writer.FinalizeFile();

        Assert.Empty(GetFragmentSamples(writer));
        Assert.Null(GetPrivateField(writer, "_pendingVideo"));
        Assert.Equal(0, ReadPrivateInt(writer, "_fragmentBufferedBytes"));
        Assert.Equal(3U, ReadPrivateUInt(writer, "_fragmentSequenceNumber"));
    }

    [Theory]
    [InlineData(FragmentFailurePhase.Moof)]
    [InlineData(FragmentFailurePhase.MdatHeader)]
    [InlineData(FragmentFailurePhase.MidPayload)]
    public void OutputFailureDoesNotCommitFragmentState(FragmentFailurePhase phase)
    {
        var fixture = LargeFixture.Create();
        var baseline = WriteSuccessfulFixture(fixture);
        var boxes = ReadTopLevelBoxes(baseline);
        var moof = boxes.First(box => box.Type == "moof");
        var mdat = boxes.SkipWhile(box => box != moof).Skip(1).First();
        var throwAfterBytes = phase switch
        {
            FragmentFailurePhase.Moof => moof.Start + 5,
            FragmentFailurePhase.MdatHeader => mdat.Start + 4,
            FragmentFailurePhase.MidPayload => mdat.PayloadOffset + 17,
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        using var output = new ThrowAfterNonSeekableStream(throwAfterBytes);
        using var writer = CreateWriter(output, maximumBytes: fixture.BufferedBytes + 64);
        WriteFirstFragmentInputs(writer, fixture);

        var error = Assert.Throws<IOException>(() =>
            WriteVideo(writer, fixture.NextKeyFrame, 80, true));

        Assert.Contains("fragment output failure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(throwAfterBytes, output.Length);
        Assert.Equal(3, GetFragmentSamples(writer).Count);
        Assert.Equal(fixture.BufferedBytes, ReadPrivateInt(writer, "_fragmentBufferedBytes"));
        Assert.Equal(1U, ReadPrivateUInt(writer, "_fragmentSequenceNumber"));
        Assert.True(ReadPrivateBool(writer, "_fragmentedHasVideoSample"));
        Assert.Null(GetPrivateField(writer, "_pendingVideo"));
    }

    private static Mp4Writer CreateWriter(Stream output, int maximumBytes)
    {
        return new Mp4Writer(
            output,
            new Mp4WriterOptions
            {
                Mode = Mp4WriteMode.Fragmented,
                MaximumFragmentBufferBytes = maximumBytes
            });
    }

    private static void WriteFirstFragmentInputs(Mp4Writer writer, LargeFixture fixture)
    {
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
        writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
            fixture.KeyFrameAnnexB,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(40),
            true));
        writer.WriteAudioSample(TestMedia.Audio(fixture.Audio, TimeSpan.Zero));
        WriteVideo(writer, fixture.NonKeyFrame, 40, false);
    }

    private static void WriteVideo(Mp4Writer writer, byte[] data, int milliseconds, bool keyFrame)
    {
        writer.WriteVideoNalUnit(TestMedia.Video(
            data,
            TimeSpan.FromMilliseconds(milliseconds),
            TimeSpan.FromMilliseconds(milliseconds),
            keyFrame));
    }

    private static byte[] WriteSuccessfulFixture(LargeFixture fixture)
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output, fixture.BufferedBytes + 64))
        {
            WriteFirstFragmentInputs(writer, fixture);
            WriteVideo(writer, fixture.NextKeyFrame, 80, true);
            writer.FinalizeFile();
        }

        return output.ToArray();
    }

    private static IList GetFragmentSamples(Mp4Writer writer)
    {
        return Assert.IsAssignableFrom<IList>(GetPrivateField(writer, "_fragmentSamples"));
    }

    private static object? GetPrivateField(Mp4Writer writer, string name)
    {
        var field = typeof(Mp4Writer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(writer);
    }

    private static int ReadPrivateInt(Mp4Writer writer, string name)
    {
        return Assert.IsType<int>(GetPrivateField(writer, name));
    }

    private static uint ReadPrivateUInt(Mp4Writer writer, string name)
    {
        return Assert.IsType<uint>(GetPrivateField(writer, name));
    }

    private static bool ReadPrivateBool(Mp4Writer writer, string name)
    {
        return Assert.IsType<bool>(GetPrivateField(writer, name));
    }

    private static MemberInfo? FindPayloadSourceMember(Type fragmentSampleType)
    {
        return GetDataMembers(fragmentSampleType)
            .Where(member =>
                member.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase) &&
                GetMemberType(member) != typeof(byte[]))
            .OrderBy(member => member is PropertyInfo ? 0 : 1)
            .FirstOrDefault();
    }

    private static IEnumerable<MemberInfo> GetDataMembers(Type type)
    {
        return type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member is FieldInfo or PropertyInfo);
    }

    private static Type GetMemberType(MemberInfo member)
    {
        return member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => throw new ArgumentOutOfRangeException(nameof(member))
        };
    }

    private static object? GetMemberValue(MemberInfo member, object instance)
    {
        return member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => throw new ArgumentOutOfRangeException(nameof(member))
        };
    }

    private static int ReadNamedInt(object instance, params string[] names)
    {
        foreach (var member in GetDataMembers(instance.GetType()))
        {
            if (GetMemberType(member) == typeof(int) &&
                names.Contains(member.Name, StringComparer.OrdinalIgnoreCase))
            {
                return (int)GetMemberValue(member, instance)!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            instance.GetType().FullName + " must expose a logical encoded-size integer.");
    }

    private static IEnumerable<object> FindRangeItems(object payloadSource)
    {
        foreach (var member in GetDataMembers(payloadSource.GetType()))
        {
            if (GetMemberType(member) == typeof(byte[]) ||
                GetMemberValue(member, payloadSource) is not IEnumerable values)
            {
                continue;
            }

            var items = values.Cast<object>().ToArray();
            if (items.Length != 0)
            {
                return items;
            }
        }

        throw new Xunit.Sdk.XunitException(
            payloadSource.GetType().FullName + " must expose retained video NAL ranges.");
    }

    private static byte[] ReadBackingArray(object range)
    {
        return ReadRangeMember<byte[]>(
            range,
            "BackingArray",
            "Backing",
            "Buffer",
            "Data",
            "Array");
    }

    private static int ReadOffset(object range)
    {
        return ReadRangeMember<int>(range, "Offset");
    }

    private static int ReadCount(object range)
    {
        return ReadRangeMember<int>(range, "Count", "Length");
    }

    private static T ReadRangeMember<T>(object range, params string[] names)
    {
        foreach (var member in GetDataMembers(range.GetType()))
        {
            if (GetMemberType(member) == typeof(T) &&
                names.Contains(member.Name, StringComparer.OrdinalIgnoreCase))
            {
                return (T)GetMemberValue(member, range)!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            range.GetType().FullName + " must expose " + string.Join("/", names) + ".");
    }

    private static IReadOnlyList<TestBox> ReadTopLevelBoxes(byte[] bytes)
    {
        var result = new List<TestBox>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size32 = ReadUInt32(bytes, offset);
            var headerSize = size32 == 1 ? 16 : 8;
            var size = size32 == 1
                ? checked((int)ReadUInt64(bytes, offset + 8))
                : checked((int)size32);
            Assert.InRange(size, headerSize, bytes.Length - offset);
            result.Add(new TestBox(
                Encoding.ASCII.GetString(bytes, offset + 4, 4),
                offset,
                offset + headerSize,
                offset + size));
            offset += size;
        }

        Assert.Equal(bytes.Length, offset);
        return result;
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        return ((ulong)ReadUInt32(bytes, offset) << 32) | ReadUInt32(bytes, offset + 4);
    }

    public enum FragmentFailurePhase
    {
        Moof,
        MdatHeader,
        MidPayload
    }

    private sealed class LargeFixture
    {
        private LargeFixture(
            byte[] keyFrameAnnexB,
            byte[][] firstKeyFramePayloads,
            byte[] audio,
            byte[] nonKeyFrame,
            byte[] nextKeyFrame)
        {
            KeyFrameAnnexB = keyFrameAnnexB;
            FirstKeyFramePayloads = firstKeyFramePayloads;
            Audio = audio;
            NonKeyFrame = nonKeyFrame;
            NextKeyFrame = nextKeyFrame;
            BufferedBytes =
                firstKeyFramePayloads.Sum(payload => payload.Length + 4) +
                audio.Length +
                nonKeyFrame.Length + 4;
            AllVideoPayloads = firstKeyFramePayloads
                .Concat(new[] { nonKeyFrame, nextKeyFrame })
                .ToArray();
        }

        public byte[] KeyFrameAnnexB { get; }
        public byte[][] FirstKeyFramePayloads { get; }
        public byte[] Audio { get; }
        public byte[] NonKeyFrame { get; }
        public byte[] NextKeyFrame { get; }
        public int BufferedBytes { get; }
        public IReadOnlyList<byte[]> AllVideoPayloads { get; }

        public static LargeFixture Create()
        {
            var first = Enumerable.Repeat((byte)0x31, 64 * 1024).ToArray();
            first[0] = 0x65;
            var second = Enumerable.Repeat((byte)0x32, 16 * 1024).ToArray();
            second[0] = 0x06;
            var annexB = new byte[4 + first.Length + 3 + second.Length];
            new byte[] { 0, 0, 0, 1 }.CopyTo(annexB, 0);
            first.CopyTo(annexB, 4);
            new byte[] { 0, 0, 1 }.CopyTo(annexB, 4 + first.Length);
            second.CopyTo(annexB, 7 + first.Length);
            var audio = Enumerable.Repeat((byte)0x21, 24 * 1024).ToArray();
            var nonKey = Enumerable.Repeat((byte)0x41, 32 * 1024).ToArray();
            var nextKey = Enumerable.Repeat((byte)0x65, 8 * 1024).ToArray();
            return new LargeFixture(
                annexB,
                new[] { first, second },
                audio,
                nonKey,
                nextKey);
        }
    }

    private sealed class CollectingNonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();

        public byte[] Bytes => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void WriteByte(byte value) => _inner.WriteByte(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowAfterNonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new MemoryStream();
        private readonly long _throwAfterBytes;

        public ThrowAfterNonSeekableStream(long throwAfterBytes)
        {
            _throwAfterBytes = throwAfterBytes;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var remaining = _throwAfterBytes - _inner.Length;
            if (remaining <= 0)
            {
                throw new IOException("Injected fragment output failure.");
            }

            if (count > remaining)
            {
                _inner.Write(buffer, offset, checked((int)remaining));
                throw new IOException("Injected fragment output failure.");
            }

            _inner.Write(buffer, offset, count);
        }

        public override void WriteByte(byte value)
        {
            if (_inner.Length >= _throwAfterBytes)
            {
                throw new IOException("Injected fragment output failure.");
            }

            _inner.WriteByte(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TestBox
    {
        public TestBox(string type, int start, int payloadOffset, int end)
        {
            Type = type;
            Start = start;
            PayloadOffset = payloadOffset;
            End = end;
        }

        public string Type { get; }
        public int Start { get; }
        public int PayloadOffset { get; }
        public int End { get; }
    }
}

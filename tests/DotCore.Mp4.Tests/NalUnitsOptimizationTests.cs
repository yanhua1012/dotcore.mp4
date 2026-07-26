using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// NAL 單元解析與記憶體配置最佳化測試套件。
/// </summary>
public sealed class NalUnitsOptimizationTests
{
    [Fact]
    public void RawNormalizationReturnsReadonlyRangeOverTheOwnedSource()
    {
        var source = new byte[] { 0x65, 0x01, 0x02 };

        var item = Normalize(source).Single();

        AssertRangeRepresentation(item);
        var range = ReadRange(item);
        Assert.Same(source, range.Backing);
        Assert.Equal(0, range.Offset);
        Assert.Equal(source.Length, range.Count);
    }

    [Fact]
    public void AnnexBNormalizationReturnsOrderedRangesOverOneBackingArray()
    {
        var source = new byte[]
        {
            0, 0, 0, 1, 0x65, 0x11,
            0, 0, 1, 0x06,
            0, 0, 0, 1, 0x41, 0x22, 0x33
        };

        var items = Normalize(source);

        Assert.Equal(3, items.Count);
        Assert.All(items, AssertRangeRepresentation);
        AssertRanges(
            source,
            items,
            new RangeExpectation(4, 2),
            new RangeExpectation(9, 1),
            new RangeExpectation(14, 3));
    }

    [Fact]
    public void ManyTinyAnnexBNalsUseValueRangesWithoutPayloadArrays()
    {
        const int count = 256;
        var source = new byte[count * 4];
        for (var index = 0; index < count; index++)
        {
            source[index * 4] = 0;
            source[index * 4 + 1] = 0;
            source[index * 4 + 2] = 1;
            source[index * 4 + 3] = (byte)(index + 1);
        }

        var items = Normalize(source);

        Assert.Equal(count, items.Count);
        Assert.All(items, item =>
        {
            AssertRangeRepresentation(item);
            var range = ReadRange(item);
            Assert.Same(source, range.Backing);
            Assert.Equal(1, range.Count);
        });
    }

    [Fact]
    public void RawAndAnnexBBoundariesPreserveExistingDiagnostics()
    {
        var empty = Normalize(Array.Empty<byte>());
        Assert.Single(empty);
        Assert.Equal(0, ReadCompatibleRange(empty[0]).Count);

        AssertCompatibleRanges(
            new byte[] { 0, 0, 0 },
            new RangeExpectation(0, 3));
        AssertCompatibleRanges(
            new byte[] { 0x7f, 0, 0, 1, 0x65 },
            new RangeExpectation(0, 5));
        AssertCompatibleRanges(
            new byte[] { 0, 0, 1, 0x65 },
            new RangeExpectation(3, 1));
        AssertCompatibleRanges(
            new byte[] { 0, 0, 0, 1, 0x65 },
            new RangeExpectation(4, 1));
        AssertCompatibleRanges(
            new byte[] { 0, 0, 1, 0x65, 0, 0 },
            new RangeExpectation(3, 3));

        AssertNormalizeFormatFailure(new byte[] { 0, 0, 1 }, "empty NAL");
        AssertNormalizeFormatFailure(
            new byte[] { 0, 0, 1, 0x65, 0, 0, 0, 1 },
            "empty NAL");
        AssertNormalizeFormatFailure(
            new byte[] { 0, 0, 1, 0, 0, 0, 1, 0x65 },
            "empty NAL");

        var nullFailure = Assert.Throws<TargetInvocationException>(
            () => NormalizeMethod.Invoke(null, new object?[] { null }));
        var nullArgument = Assert.IsType<ArgumentNullException>(nullFailure.InnerException);
        Assert.Equal("data", nullArgument.ParamName);
    }

    [Fact]
    public void ParameterSetValidationSupportsBothStartCodesAndRejectsInvalidSets()
    {
        var sps = new byte[] { 0, 0, 1, 0x67, 0x64, 0x00 };
        var pps = new byte[] { 0, 0, 0, 1, 0x68, 0xee };
        var originalSps = sps.ToArray();
        var originalPps = pps.ToArray();

        var configuration = VideoCodecConfiguration.CreateH264(sps, pps);
        Array.Fill(sps, (byte)0xcc);
        Array.Fill(pps, (byte)0xdd);

        Assert.Equal(originalSps, configuration.Sps);
        Assert.Equal(originalPps, configuration.Pps);

        var multiple = Assert.Throws<ArgumentException>(() =>
            VideoCodecConfiguration.CreateH264(
                new byte[] { 0, 0, 1, 0x67, 0, 0, 1, 0x67 },
                originalPps));
        Assert.Equal("sps", multiple.ParamName);

        var wrongType = Assert.Throws<ArgumentException>(() =>
            VideoCodecConfiguration.CreateH264(
                new byte[] { 0, 0, 1, 0x68 },
                originalPps));
        Assert.Equal("sps", wrongType.ParamName);

        var empty = Assert.Throws<ArgumentException>(() =>
            VideoCodecConfiguration.CreateH264(Array.Empty<byte>(), originalPps));
        Assert.Equal("sps", empty.ParamName);
    }

    [Theory]
    [InlineData(Mp4WriteMode.Progressive)]
    [InlineData(Mp4WriteMode.FastStart)]
    public void WriterSnapshotsCallerDataBeforeRangeNormalization(Mp4WriteMode mode)
    {
        var caller = new byte[] { 0, 0, 0, 1, 0x65, 0x11, 0, 0, 1, 0x06, 0x22 };
        var expected = new[]
        {
            new byte[] { 0x65, 0x11 },
            new byte[] { 0x06, 0x22 }
        };
        var sample = TestMedia.Video(caller, TimeSpan.Zero, TimeSpan.Zero);
        Array.Fill(caller, (byte)0xcc);
        var exposed = sample.Data;
        Array.Fill(exposed, (byte)0xdd);
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output, new Mp4WriterOptions { Mode = mode }))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(sample);
            writer.FinalizeFile();
        }

        using var reader = new Mp4Reader(output);
        Assert.Equal(expected, reader.ReadVideoNalUnits().Select(value => value.Data));
    }

    [Fact]
    public void InvalidAnnexBSubmissionDoesNotMutateWriterState()
    {
        using var output = new MemoryStream();
        using var writer = new Mp4Writer(output);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x65, 0x01 },
            TimeSpan.Zero,
            TimeSpan.Zero));

        var error = Assert.Throws<Mp4FormatException>(() =>
            writer.WriteVideoNalUnit(TestMedia.Video(
                new byte[] { 0, 0, 1, 0, 0, 0, 1, 0x41 },
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(80),
                false)));
        Assert.Contains("empty NAL", error.Message, StringComparison.OrdinalIgnoreCase);

        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x41, 0x02 },
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(40),
            false));
        writer.FinalizeFile();

        using var reader = new Mp4Reader(output);
        var samples = reader.ReadVideoNalUnits().ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Equal(new byte[] { 0x65, 0x01 }, samples[0].Data);
        Assert.Equal(new byte[] { 0x41, 0x02 }, samples[1].Data);
        Assert.Equal(TimeSpan.FromMilliseconds(40), samples[1].DecodeTimestamp);
    }

    [Fact]
    public void NalLengthOverflowIsRejectedWithoutMutatingWriterState()
    {
        var configuration = VideoCodecConfiguration.CreateH264(
            new byte[] { 0x67, 0x64 },
            new byte[] { 0x68, 0xee },
            nalLengthSize: 1);
        using var output = new MemoryStream();
        using var writer = new Mp4Writer(output);
        writer.SetVideoCodecConfiguration(configuration);
        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x65, 0x01 },
            TimeSpan.Zero,
            TimeSpan.Zero));
        var oversized = Enumerable.Repeat((byte)0x41, 256).ToArray();

        var error = Assert.Throws<Mp4FormatException>(() =>
            writer.WriteVideoNalUnit(TestMedia.Video(
                oversized,
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(80),
                false)));
        Assert.Contains("length field", error.Message, StringComparison.OrdinalIgnoreCase);

        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x41, 0x02 },
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(40),
            false));
        writer.FinalizeFile();

        using var reader = new Mp4Reader(output);
        Assert.Equal(
            new[] { "6501", "4102" },
            reader.ReadVideoNalUnits().Select(sample => Convert.ToHexString(sample.Data)));
    }

    [Theory]
    [InlineData(Mp4WriteMode.Progressive, "ftyp,mdat,moov")]
    [InlineData(Mp4WriteMode.FastStart, "ftyp,moov,mdat")]
    public void FixedRawAndAnnexBOutputMatchesExactPreChangePayload(
        Mp4WriteMode mode,
        string expectedTopLevel)
    {
        var bytes = WriteFixedOutput(mode);

        Assert.Equal(expectedTopLevel.Split(','), ReadTopLevelBoxTypes(bytes));
        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 2, 0x65, 0x10,
                0, 0, 0, 2, 0x06, 0x20,
                0, 0, 0, 2, 0x41, 0x30
            },
            ReadMdatPayload(bytes));

        using var reader = new Mp4Reader(new MemoryStream(bytes));
        Assert.Equal(
            new[] { "6510", "0620", "4130" },
            reader.ReadVideoNalUnits().Select(sample => Convert.ToHexString(sample.Data)));
    }

    private static MethodInfo NormalizeMethod =>
        typeof(NalUnits).GetMethod(
            "Normalize",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    private static IReadOnlyList<object> Normalize(byte[] data)
    {
        var result = NormalizeMethod.Invoke(null, new object?[] { data });
        Assert.NotNull(result);
        return ((IEnumerable)result!).Cast<object>().ToArray();
    }

    private static void AssertRangeRepresentation(object item)
    {
        Assert.NotEqual(typeof(byte[]), item.GetType());
        Assert.True(item.GetType().IsValueType, "Each normalized NAL must be represented by a value range, not a per-NAL object.");
        Assert.True(
            item.GetType().CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == typeof(IsReadOnlyAttribute).FullName),
            "The normalized NAL range value must be readonly.");
        var range = ReadRange(item);
        Assert.NotNull(range.Backing);
    }

    private static RangeView ReadRange(object item)
    {
        var type = item.GetType();
        var backing = ReadMember<byte[]>(item, type, "BackingArray", "Backing", "Buffer", "Data", "Array");
        var offset = ReadMember<int>(item, type, "Offset");
        var count = ReadMember<int>(item, type, "Count", "Length");
        return new RangeView(backing, offset, count);
    }

    private static RangeView ReadCompatibleRange(object item)
    {
        return item is byte[] payload
            ? new RangeView(payload, 0, payload.Length)
            : ReadRange(item);
    }

    private static T ReadMember<T>(object instance, Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(T))
            {
                return (T)property.GetValue(instance)!;
            }

            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(T))
            {
                return (T)field.GetValue(instance)!;
            }
        }

        throw new Xunit.Sdk.XunitException(
            type.FullName + " must expose an internal " + typeof(T).Name +
            " member named " + string.Join("/", names) + ".");
    }

    private static void AssertCompatibleRanges(
        byte[] source,
        params RangeExpectation[] expected)
    {
        var items = Normalize(source);
        Assert.Equal(expected.Length, items.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var actual = ReadCompatibleRange(items[index]);
            Assert.Equal(
                source.Skip(expected[index].Offset).Take(expected[index].Count),
                actual.Backing.Skip(actual.Offset).Take(actual.Count));
        }
    }

    private static void AssertRanges(
        byte[] source,
        IReadOnlyList<object> items,
        params RangeExpectation[] expected)
    {
        Assert.Equal(expected.Length, items.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var actual = ReadRange(items[index]);
            Assert.Same(source, actual.Backing);
            Assert.Equal(expected[index].Offset, actual.Offset);
            Assert.Equal(expected[index].Count, actual.Count);
        }
    }

    private static void AssertNormalizeFormatFailure(byte[] source, string diagnostic)
    {
        var invocation = Assert.Throws<TargetInvocationException>(() => Normalize(source));
        var error = Assert.IsType<Mp4FormatException>(invocation.InnerException);
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] WriteFixedOutput(Mp4WriteMode mode)
    {
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output, new Mp4WriterOptions { Mode = mode }))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(TestMedia.Video(
                new byte[] { 0x65, 0x10 },
                TimeSpan.Zero,
                TimeSpan.Zero));
            writer.WriteVideoNalUnit(TestMedia.Video(
                new byte[]
                {
                    0, 0, 0, 1, 0x06, 0x20,
                    0, 0, 1, 0x41, 0x30
                },
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(40),
                false));
            writer.FinalizeFile();
        }

        return output.ToArray();
    }

    private static IReadOnlyList<string> ReadTopLevelBoxTypes(byte[] bytes)
    {
        var result = new List<string>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size32 = ReadUInt32(bytes, offset);
            var headerSize = size32 == 1 ? 16 : 8;
            var size = size32 == 1
                ? checked((int)ReadUInt64(bytes, offset + 8))
                : checked((int)size32);
            Assert.InRange(size, headerSize, bytes.Length - offset);
            result.Add(Encoding.ASCII.GetString(bytes, offset + 4, 4));
            offset += size;
        }

        Assert.Equal(bytes.Length, offset);
        return result;
    }

    private static byte[] ReadMdatPayload(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var size32 = ReadUInt32(bytes, offset);
            var headerSize = size32 == 1 ? 16 : 8;
            var size = size32 == 1
                ? checked((int)ReadUInt64(bytes, offset + 8))
                : checked((int)size32);
            if (Encoding.ASCII.GetString(bytes, offset + 4, 4) == "mdat")
            {
                return bytes.Skip(offset + headerSize).Take(size - headerSize).ToArray();
            }

            offset += size;
        }

        throw new Xunit.Sdk.XunitException("The fixed output did not contain mdat.");
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

    private readonly struct RangeExpectation
    {
        public RangeExpectation(int offset, int count)
        {
            Offset = offset;
            Count = count;
        }

        public int Offset { get; }
        public int Count { get; }
    }

    private readonly struct RangeView
    {
        public RangeView(byte[] backing, int offset, int count)
        {
            Backing = backing;
            Offset = offset;
            Count = count;
        }

        public byte[] Backing { get; }
        public int Offset { get; }
        public int Count { get; }
    }
}

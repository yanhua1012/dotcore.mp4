using System.Reflection;
using System.Text;
using DotCore.Mp4;
using Xunit;

/// <summary>
/// Movie Fragment 建立器與片段構建測試套件。
/// </summary>
public sealed class MovieFragmentBuilderTests
{
    [Fact]
    public void FlushCallsMovieFragmentBuilderExactlyOnce()
    {
        var flush = typeof(Mp4Writer).GetMethod(
            "FlushFragment",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var builders = typeof(Mp4Writer)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.Name == "BuildMovieFragment")
            .ToArray();

        Assert.NotNull(flush);
        var builder = Assert.Single(builders);
        Assert.Equal(
            1,
            CountMethodCalls(flush!, builder));
    }

    [Fact]
    public void MovieFragmentBuilderPreallocatesAndReturnsPatchableBufferContract()
    {
        var builder = Assert.Single(
            typeof(Mp4Writer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.Name == "BuildMovieFragment");
        var memoryStreamCapacityConstructor = typeof(MemoryStream)
            .GetConstructor(new[] { typeof(int) });

        Assert.NotNull(memoryStreamCapacityConstructor);
        Assert.True(
            ContainsNewObjectCall(builder, memoryStreamCapacityConstructor!),
            "The normal movie-fragment build must preallocate a checked conservative buffer capacity.");
        Assert.NotEqual(typeof(byte[]), builder.ReturnType);
        Assert.True(
            HasByteArrayMember(builder.ReturnType) &&
            HasValidLengthMember(builder.ReturnType),
            "The movie-fragment builder must return an internal backing-array plus valid-length segment.");

        var trackBuilder = typeof(Mp4Writer).GetMethod(
            "WriteTrackFragment",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(trackBuilder);
        Assert.DoesNotContain(
            trackBuilder!.GetParameters(),
            parameter => parameter.Name != null &&
                         parameter.Name.Contains("dataOffset", StringComparison.OrdinalIgnoreCase));
        Assert.True(
            trackBuilder.ReturnType == typeof(long) ||
            HasPatchPositionMember(builder.ReturnType),
            "Each trun build must report its data_offset patch position.");
    }

    [Fact]
    public void CheckedBigEndianPatchHelperAcceptsLongInputsAndWritesInPlace()
    {
        var patch = FindPatchHelper();

        Assert.NotNull(patch);
        var buffer = Enumerable.Repeat((byte)0xcc, 8).ToArray();
        InvokePatch(patch!, buffer, validLength: 8, position: 2, value: 0x01020304);

        Assert.Equal(
            new byte[] { 0xcc, 0xcc, 0x01, 0x02, 0x03, 0x04, 0xcc, 0xcc },
            buffer);

        InvokePatch(patch!, buffer, validLength: 8, position: 0, value: int.MinValue);
        Assert.Equal(new byte[] { 0x80, 0, 0, 0 }, buffer.Take(4));
        InvokePatch(patch!, buffer, validLength: 8, position: 4, value: int.MaxValue);
        Assert.Equal(new byte[] { 0x7f, 0xff, 0xff, 0xff }, buffer.Skip(4));
    }

    [Theory]
    [InlineData(-1L, 0L, "position")]
    [InlineData(5L, 0L, "position")]
    [InlineData(long.MaxValue, 0L, "position")]
    [InlineData(0L, 2147483648L, "offset")]
    [InlineData(0L, -2147483649L, "offset")]
    public void CheckedBigEndianPatchHelperRejectsInvalidBoundaryWithoutMutation(
        long position,
        long value,
        string diagnostic)
    {
        var patch = FindPatchHelper();
        Assert.NotNull(patch);
        var buffer = Enumerable.Repeat((byte)0xcc, 8).ToArray();
        var before = buffer.ToArray();

        var invocation = Assert.Throws<TargetInvocationException>(() =>
            InvokePatch(patch!, buffer, validLength: 8, position, value));

        Assert.NotNull(invocation.InnerException);
        Assert.Contains(diagnostic, invocation.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, buffer);
    }

    [Fact]
    public void VideoOnlyMultipleSampleFragmentMatchesExactTrunBytes()
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                new byte[] { 0x65, 0x01 },
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(40),
                true));
            writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                new byte[] { 0x41, 0x02 },
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(40),
                false));
            writer.FinalizeFile();
        }

        var bytes = output.ToArray();
        var boxes = ReadTopLevelBoxes(bytes);
        Assert.Equal(new[] { "ftyp", "moov", "moof", "mdat" }, boxes.Select(box => box.Type));
        var moof = boxes.Single(box => box.Type == "moof");
        var mdat = boxes.Single(box => box.Type == "mdat");
        var trunStart = Assert.Single(FindBoxStarts(bytes, "trun"));
        var dataOffset = checked(moof.Size + 8);
        Assert.Equal(
            BuildExpectedTrun(
                version: 1,
                dataOffset,
                new TrunSample(400_000, 6, 0x02000000, 0),
                new TrunSample(400_000, 6, 0x01010000, -200_000)),
            bytes.Skip(trunStart).Take(52));
        Assert.Equal(moof.Start + dataOffset, mdat.PayloadOffset);
        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 2, 0x65, 0x01,
                0, 0, 0, 2, 0x41, 0x02
            },
            bytes.Skip(mdat.PayloadOffset).Take(mdat.Size - 8));
    }

    [Fact]
    public void MixedTrackFragmentPatchesExactVideoAndAudioOffsetsAndBytes()
    {
        using var output = new MemoryStream();
        using (var writer = CreateWriter(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(
                new byte[] { 0x65, 0x01 },
                TimeSpan.Zero,
                TimeSpan.Zero));
            writer.WriteAudioSample(TestMedia.Audio(
                new byte[] { 0x21, 0x10 },
                TimeSpan.Zero));
            writer.FinalizeFile();
        }

        var bytes = output.ToArray();
        var boxes = ReadTopLevelBoxes(bytes);
        var moof = boxes.Single(box => box.Type == "moof");
        var mdat = boxes.Single(box => box.Type == "mdat");
        var truns = FindBoxStarts(bytes, "trun");
        Assert.Equal(2, truns.Count);
        var videoOffset = checked(moof.Size + 8);
        var audioOffset = checked(videoOffset + 6);

        Assert.Equal(
            BuildExpectedTrun(
                version: 0,
                videoOffset,
                new TrunSample(400_000, 6, 0x02000000, 0)),
            bytes.Skip(truns[0]).Take(36));
        Assert.Equal(
            BuildExpectedTrun(
                version: 0,
                audioOffset,
                new TrunSample(200_000, 2, 0x02000000, 0)),
            bytes.Skip(truns[1]).Take(36));
        Assert.Equal(moof.Start + videoOffset, mdat.PayloadOffset);
        Assert.Equal(moof.Start + audioOffset, mdat.PayloadOffset + 6);
        Assert.Equal(
            new byte[] { 0, 0, 0, 2, 0x65, 0x01, 0x21, 0x10 },
            bytes.Skip(mdat.PayloadOffset).Take(mdat.Size - 8));
    }

    [Fact]
    public void FragmentSequenceOverflowIsRejectedBeforeWritingMoof()
    {
        using var output = new MemoryStream();
        using var writer = CreateWriter(output);
        writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
        writer.WriteVideoNalUnit(TestMedia.Video(
            new byte[] { 0x65, 0x01 },
            TimeSpan.Zero,
            TimeSpan.Zero));
        var headerLength = output.Length;
        var sequence = typeof(Mp4Writer).GetField(
            "_fragmentSequenceNumber",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sequence);
        sequence!.SetValue(writer, uint.MaxValue);

        var error = Assert.Throws<Mp4FormatException>(() => writer.FinalizeFile());

        Assert.Contains("sequence", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(headerLength, output.Length);
        Assert.Equal(uint.MaxValue, sequence.GetValue(writer));
    }

    private static Mp4Writer CreateWriter(Stream output)
    {
        return new Mp4Writer(
            output,
            new Mp4WriterOptions { Mode = Mp4WriteMode.Fragmented });
    }

    private static MethodInfo? FindPatchHelper()
    {
        return typeof(Mp4Writer).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Where(method =>
                method.Name.Contains("Patch", StringComparison.OrdinalIgnoreCase) &&
                method.ReturnType == typeof(void))
            .SingleOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == 4 &&
                       parameters[0].ParameterType == typeof(byte[]) &&
                       (parameters[1].ParameterType == typeof(int) ||
                        parameters[1].ParameterType == typeof(long)) &&
                       parameters[2].ParameterType == typeof(long) &&
                       parameters[3].ParameterType == typeof(long);
            });
    }

    private static void InvokePatch(
        MethodInfo patch,
        byte[] buffer,
        long validLength,
        long position,
        long value)
    {
        var lengthType = patch.GetParameters()[1].ParameterType;
        patch.Invoke(
            null,
            new object[]
            {
                buffer,
                lengthType == typeof(int) ? (object)checked((int)validLength) : validLength,
                position,
                value
            });
    }

    private static bool HasByteArrayMember(Type type)
    {
        return GetDataMembers(type).Any(member => GetMemberType(member) == typeof(byte[]));
    }

    private static bool HasValidLengthMember(Type type)
    {
        return GetDataMembers(type).Any(member =>
            GetMemberType(member) == typeof(int) &&
            (member.Name.Contains("Length", StringComparison.OrdinalIgnoreCase) ||
             member.Name.Contains("Count", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasPatchPositionMember(Type type)
    {
        return GetDataMembers(type).Any(member =>
            member.Name.Contains("Patch", StringComparison.OrdinalIgnoreCase) &&
            typeof(System.Collections.IEnumerable).IsAssignableFrom(GetMemberType(member)));
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

    private static int CountMethodCalls(MethodInfo caller, MethodInfo target)
    {
        var il = caller.GetMethodBody()!.GetILAsByteArray()!;
        var token = BitConverter.GetBytes(target.MetadataToken);
        var count = 0;
        for (var index = 0; index <= il.Length - 5; index++)
        {
            if ((il[index] == 0x28 || il[index] == 0x6f) &&
                il[index + 1] == token[0] &&
                il[index + 2] == token[1] &&
                il[index + 3] == token[2] &&
                il[index + 4] == token[3])
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsNewObjectCall(MethodInfo method, ConstructorInfo constructor)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var index = 0; index <= il.Length - 5; index++)
        {
            if (il[index] != 0x73)
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, index + 1);
            if (method.Module.ResolveMethod(token) is ConstructorInfo called &&
                called.DeclaringType?.FullName == constructor.DeclaringType?.FullName &&
                called.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(
                        constructor.GetParameters().Select(parameter => parameter.ParameterType.FullName)))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] BuildExpectedTrun(
        byte version,
        int dataOffset,
        params TrunSample[] samples)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, checked((uint)(20 + samples.Length * 16)));
        stream.Write(Encoding.ASCII.GetBytes("trun"));
        stream.WriteByte(version);
        stream.WriteByte(0x00);
        stream.WriteByte(0x0f);
        stream.WriteByte(0x01);
        WriteUInt32(stream, (uint)samples.Length);
        WriteUInt32(stream, unchecked((uint)dataOffset));
        foreach (var sample in samples)
        {
            WriteUInt32(stream, (uint)sample.Duration);
            WriteUInt32(stream, (uint)sample.Size);
            WriteUInt32(stream, sample.Flags);
            WriteUInt32(stream, unchecked((uint)sample.CompositionOffset));
        }

        return stream.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
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

    private static IReadOnlyList<int> FindBoxStarts(byte[] bytes, string type)
    {
        var marker = Encoding.ASCII.GetBytes(type);
        var result = new List<int>();
        for (var offset = 4; offset <= bytes.Length - 4; offset++)
        {
            if (bytes.Skip(offset).Take(4).SequenceEqual(marker))
            {
                result.Add(offset - 4);
            }
        }

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

    private readonly struct TrunSample
    {
        public TrunSample(int duration, int size, uint flags, int compositionOffset)
        {
            Duration = duration;
            Size = size;
            Flags = flags;
            CompositionOffset = compositionOffset;
        }

        public int Duration { get; }
        public int Size { get; }
        public uint Flags { get; }
        public int CompositionOffset { get; }
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
        public int Size => End - Start;
    }
}

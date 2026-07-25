using System.Reflection;
using DotCore.Mp4;
using Xunit;

namespace DotCore.Mp4.Tests;

public sealed class ReaderOwnershipTests
{
    [Fact]
    public void VideoOwnershipTransferFactoryIsInternalNamedAndTakesOwnership()
    {
        var factory = FindOwnedFactory(typeof(EncodedVideoNalUnit));

        Assert.NotNull(factory);
        Assert.Contains("Owned", factory!.Name, StringComparison.OrdinalIgnoreCase);
        var owned = new byte[] { 0x65, 0x01 };
        var sample = (EncodedVideoNalUnit)factory.Invoke(
            null,
            new object[]
            {
                owned,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(40),
                true
            })!;

        Assert.Same(owned, sample.DataBytes);
        Assert.NotSame(owned, sample.Data);
    }

    [Fact]
    public void AudioOwnershipTransferFactoryIsInternalNamedAndTakesOwnership()
    {
        var factory = FindOwnedFactory(typeof(EncodedAudioSample));

        Assert.NotNull(factory);
        Assert.Contains("Owned", factory!.Name, StringComparison.OrdinalIgnoreCase);
        var owned = new byte[] { 0x21, 0x10 };
        var sample = (EncodedAudioSample)factory.Invoke(
            null,
            new object[]
            {
                owned,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20)
            })!;

        Assert.Same(owned, sample.DataBytes);
        Assert.NotSame(owned, sample.Data);
    }

    [Theory]
    [InlineData(typeof(EncodedVideoNalUnit), "CreateVideoSample")]
    [InlineData(typeof(EncodedAudioSample), "CreateAudioSample")]
    public void ReaderCreationDoesNotCallPublicDefensiveCopyConstructor(
        Type sampleType,
        string readerMethodName)
    {
        var publicConstructor = sampleType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Single();
        var readerMethod = typeof(Mp4Reader).GetMethod(
            readerMethodName,
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(readerMethod);
        Assert.False(
            ContainsNewObjectCall(readerMethod!, publicConstructor),
            readerMethodName + " must use the internal ownership-transfer factory instead of the public defensive-copy constructor.");
    }

    [Fact]
    public void OwnershipTransferFactoriesPreservePublicValidationContracts()
    {
        var videoFactory = FindOwnedFactory(typeof(EncodedVideoNalUnit));
        var audioFactory = FindOwnedFactory(typeof(EncodedAudioSample));
        Assert.NotNull(videoFactory);
        Assert.NotNull(audioFactory);

        AssertEquivalentFailure(
            () => new EncodedVideoNalUnit(null!, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), true),
            videoFactory!,
            null!, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), true);
        AssertEquivalentFailure(
            () => new EncodedVideoNalUnit(Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), true),
            videoFactory!,
            Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), true);
        AssertEquivalentFailure(
            () => new EncodedVideoNalUnit(new byte[] { 1 }, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromSeconds(1), true),
            videoFactory!,
            new byte[] { 1 }, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromSeconds(1), true);
        AssertEquivalentFailure(
            () => new EncodedVideoNalUnit(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1), true),
            videoFactory!,
            new byte[] { 1 }, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1), true);
        AssertEquivalentFailure(
            () => new EncodedVideoNalUnit(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, true),
            videoFactory!,
            new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, true);

        AssertEquivalentFailure(
            () => new EncodedAudioSample(null!, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            audioFactory!,
            null!, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        AssertEquivalentFailure(
            () => new EncodedAudioSample(Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            audioFactory!,
            Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        AssertEquivalentFailure(
            () => new EncodedAudioSample(new byte[] { 1 }, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            audioFactory!,
            new byte[] { 1 }, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        AssertEquivalentFailure(
            () => new EncodedAudioSample(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1)),
            audioFactory!,
            new byte[] { 1 }, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1));
        AssertEquivalentFailure(
            () => new EncodedAudioSample(new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            audioFactory!,
            new byte[] { 1 }, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    }

    [Fact]
    public void EveryEnumerableEntryPointIsRepeatableAndMutationIsolated()
    {
        var fixture = CreateMixedFixture();
        using var input = new MemoryStream(fixture.Bytes);
        using var reader = new Mp4Reader(input);
        Array.Fill(fixture.Bytes, (byte)0xcc);

        var videoEvents = new List<byte[]>();
        var audioEvents = new List<byte[]>();
        reader.VideoNalUnitRead += (_, args) =>
        {
            videoEvents.Add(args.Data.ToArray());
            args.Data[0] ^= 0xff;
            args.Configuration.Sps[0] ^= 0xff;
        };
        reader.AacSampleRead += (_, args) =>
        {
            audioEvents.Add(args.Data.ToArray());
            args.Data[0] ^= 0xff;
            args.AudioSpecificConfig[0] ^= 0xff;
        };

        var firstVideo = reader.ReadVideoNalUnits().ToArray();
        var aliasedVideo = reader.EnumerateVideoNalUnits().ToArray();
        var firstAudio = reader.ReadAudioSamples().ToArray();
        var aliasedAudio = reader.EnumerateAacSamples().ToArray();

        AssertVideo(firstVideo, fixture.VideoPayloads);
        AssertVideo(aliasedVideo, fixture.VideoPayloads);
        AssertAudio(firstAudio, fixture.AudioPayloads);
        AssertAudio(aliasedAudio, fixture.AudioPayloads);
        Assert.Equal(fixture.VideoPayloads.Concat(fixture.VideoPayloads), videoEvents);
        Assert.Equal(fixture.AudioPayloads.Concat(fixture.AudioPayloads), audioEvents);

        var mutatedVideo = firstVideo[0].Data;
        mutatedVideo[0] ^= 0xff;
        var mutatedAudio = firstAudio[0].Data;
        mutatedAudio[0] ^= 0xff;
        Assert.Equal(fixture.VideoPayloads[0], firstVideo[0].Data);
        Assert.Equal(fixture.AudioPayloads[0], firstAudio[0].Data);
        Assert.Equal(TestMedia.H264Configuration.Sps, reader.VideoConfiguration!.Sps);
        Assert.Equal(TestMedia.AacConfiguration.AudioSpecificConfig, reader.AudioConfiguration!.AudioSpecificConfig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventOnlyEntryPointsPreserveCrossTrackOrderAndIsolation(bool useReadAllAlias)
    {
        var fixture = CreateMixedFixture();
        using var reader = new Mp4Reader(new MemoryStream(fixture.Bytes));
        var order = new List<string>();
        reader.VideoNalUnitRead += (_, args) =>
        {
            order.Add("v:" + Convert.ToHexString(args.Data));
            args.Data[0] ^= 0xff;
        };
        reader.AacSampleRead += (_, args) =>
        {
            order.Add("a:" + Convert.ToHexString(args.Data));
            args.Data[0] ^= 0xff;
        };

        if (useReadAllAlias)
        {
            reader.ReadAll();
        }
        else
        {
            reader.Read();
        }

        Assert.Equal(fixture.EventOrder, order);
        AssertVideo(reader.ReadVideoNalUnits().ToArray(), fixture.VideoPayloads);
        AssertAudio(reader.ReadAudioSamples().ToArray(), fixture.AudioPayloads);
    }

    [Fact]
    public void DeliveredSamplesOutliveReaderAndDoNotRetainTheFullSnapshotAsPayload()
    {
        var fixture = CreateMixedFixture();
        var input = new MemoryStream(fixture.Bytes);
        var reader = new Mp4Reader(input, leaveOpen: false);
        var video = reader.ReadVideoNalUnits().First();
        var audio = reader.ReadAudioSamples().First();
        var snapshot = GetReaderSnapshot(reader);

        Assert.Equal(fixture.VideoPayloads[0].Length, video.DataBytes.Length);
        Assert.Equal(fixture.AudioPayloads[0].Length, audio.DataBytes.Length);
        Assert.NotSame(snapshot, video.DataBytes);
        Assert.NotSame(snapshot, audio.DataBytes);

        reader.Dispose();
        Assert.False(input.CanRead);
        Assert.Equal(fixture.VideoPayloads[0], video.Data);
        Assert.Equal(fixture.AudioPayloads[0], audio.Data);
    }

    private static MethodInfo? FindOwnedFactory(Type sampleType)
    {
        var publicParameters = sampleType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        return sampleType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                method.ReturnType == sampleType &&
                method.Name.Contains("Owned", StringComparison.OrdinalIgnoreCase) &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(publicParameters));
    }

    private static bool ContainsNewObjectCall(MethodInfo method, ConstructorInfo constructor)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var token = BitConverter.GetBytes(constructor.MetadataToken);
        for (var index = 0; index <= il.Length - 5; index++)
        {
            if (il[index] == 0x73 &&
                il[index + 1] == token[0] &&
                il[index + 2] == token[1] &&
                il[index + 3] == token[2] &&
                il[index + 4] == token[3])
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertEquivalentFailure(
        Action publicConstruction,
        MethodInfo ownedFactory,
        params object?[] ownedArguments)
    {
        var expected = Assert.ThrowsAny<ArgumentException>(publicConstruction);
        var invocation = Assert.Throws<TargetInvocationException>(
            () => ownedFactory.Invoke(null, ownedArguments));
        var actual = Assert.IsAssignableFrom<ArgumentException>(invocation.InnerException);

        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.ParamName, actual.ParamName);
        Assert.Equal(MessageWithoutParameterSuffix(expected), MessageWithoutParameterSuffix(actual));
    }

    private static string MessageWithoutParameterSuffix(ArgumentException error)
    {
        var suffix = Environment.NewLine + "Parameter '" + error.ParamName + "'";
        return error.Message.EndsWith(suffix, StringComparison.Ordinal)
            ? error.Message.Substring(0, error.Message.Length - suffix.Length)
            : error.Message;
    }

    private static byte[] GetReaderSnapshot(Mp4Reader reader)
    {
        var field = typeof(Mp4Reader).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<byte[]>(field!.GetValue(reader));
    }

    private static void AssertVideo(
        IReadOnlyList<EncodedVideoNalUnit> actual,
        IReadOnlyList<byte[]> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index].Data);
        }
    }

    private static void AssertAudio(
        IReadOnlyList<EncodedAudioSample> actual,
        IReadOnlyList<byte[]> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index].Data);
        }
    }

    private static ReaderFixture CreateMixedFixture()
    {
        var largeVideo = Enumerable.Repeat((byte)0x31, 64 * 1024).ToArray();
        largeVideo[0] = 0x65;
        var secondVideoNal = new byte[] { 0x06, 0x05 };
        var laterVideoNal = new byte[] { 0x41, 0x02 };
        var largeAudio = Enumerable.Repeat((byte)0x21, 48 * 1024).ToArray();
        var laterAudio = new byte[] { 0x22, 0x10 };
        using var output = new MemoryStream();
        using (var writer = new Mp4Writer(output))
        {
            writer.SetVideoCodecConfiguration(TestMedia.H264Configuration);
            writer.SetAudioCodecConfiguration(TestMedia.AacConfiguration);
            writer.WriteVideoNalUnit(TestMedia.Video(largeVideo, TimeSpan.Zero, TimeSpan.Zero));
            writer.WriteVideoNalUnit(TestMedia.Video(secondVideoNal, TimeSpan.Zero, TimeSpan.Zero));
            writer.WriteAudioSample(TestMedia.Audio(largeAudio, TimeSpan.Zero));
            writer.WriteAudioSample(TestMedia.Audio(laterAudio, TimeSpan.FromMilliseconds(20)));
            writer.WriteVideoNalUnit(TestMedia.Video(
                laterVideoNal,
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(40),
                false));
            writer.FinalizeFile();
        }

        var videoPayloads = new[] { largeVideo, secondVideoNal, laterVideoNal };
        var audioPayloads = new[] { largeAudio, laterAudio };
        return new ReaderFixture(
            output.ToArray(),
            videoPayloads,
            audioPayloads,
            new[]
            {
                "v:" + Convert.ToHexString(largeVideo),
                "v:" + Convert.ToHexString(secondVideoNal),
                "a:" + Convert.ToHexString(largeAudio),
                "a:" + Convert.ToHexString(laterAudio),
                "v:" + Convert.ToHexString(laterVideoNal)
            });
    }

    private sealed class ReaderFixture
    {
        public ReaderFixture(
            byte[] bytes,
            IReadOnlyList<byte[]> videoPayloads,
            IReadOnlyList<byte[]> audioPayloads,
            IReadOnlyList<string> eventOrder)
        {
            Bytes = bytes;
            VideoPayloads = videoPayloads;
            AudioPayloads = audioPayloads;
            EventOrder = eventOrder;
        }

        public byte[] Bytes { get; }
        public IReadOnlyList<byte[]> VideoPayloads { get; }
        public IReadOnlyList<byte[]> AudioPayloads { get; }
        public IReadOnlyList<string> EventOrder { get; }
    }
}

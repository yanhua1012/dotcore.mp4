using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DotCore.Mp4;

namespace DotCore.Mp4.Benchmarks;

internal sealed class CompatibilityBaseline
{
    public int SchemaVersion { get; set; } = 1;
    public string TargetFramework { get; set; } = string.Empty;
    public List<PackageReferenceBaseline> ExplicitProductionPackages { get; set; } = new();
    public List<string> PublicApi { get; set; } = new();
}

internal sealed class PackageReferenceBaseline
{
    public string Include { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PrivateAssets { get; set; } = string.Empty;
}

internal sealed class FixedOutputBaseline
{
    public int SchemaVersion { get; set; } = 1;
    public List<FixedOutputCase> Outputs { get; set; } = new();
}

internal sealed class FixedOutputCase
{
    public string Identity { get; set; } = string.Empty;
    public int Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public List<BoxSummary> TopLevelBoxes { get; set; } = new();
    public string VideoConfiguration { get; set; } = string.Empty;
    public string AudioConfiguration { get; set; } = string.Empty;
    public List<string> VideoSamples { get; set; } = new();
    public List<string> AudioSamples { get; set; } = new();
    public List<string> EventOrder { get; set; } = new();
}

internal sealed class BoxSummary
{
    public string Type { get; set; } = string.Empty;
    public int Size { get; set; }
    public int PayloadLength { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
}

internal static class CompatibilityBaselines
{
    public const string CompatibilityFileName = "compatibility.json";
    public const string FixedOutputsFileName = "fixed-outputs.json";

    public static CompatibilityBaseline CaptureCompatibility(string repositoryRoot)
    {
        var projectPath = Path.Combine(repositoryRoot, "src", "DotCore.Mp4", "DotCore.Mp4.csproj");
        var project = XDocument.Load(projectPath, LoadOptions.None);
        var targetFramework = project.Descendants("TargetFramework").Select(element => element.Value.Trim()).Single();
        var packages = project.Descendants("PackageReference")
            .Select(element => new PackageReferenceBaseline
            {
                Include = ((string?)element.Attribute("Include") ?? string.Empty).Trim(),
                Version = (((string?)element.Attribute("Version")) ??
                           element.Elements("Version").Select(value => value.Value).SingleOrDefault() ??
                           string.Empty).Trim(),
                PrivateAssets = (element.Elements("PrivateAssets").Select(value => value.Value).SingleOrDefault() ??
                                 string.Empty).Trim()
            })
            .OrderBy(package => package.Include, StringComparer.Ordinal)
            .ThenBy(package => package.Version, StringComparer.Ordinal)
            .ToList();

        return new CompatibilityBaseline
        {
            TargetFramework = targetFramework,
            ExplicitProductionPackages = packages,
            PublicApi = CapturePublicApi(typeof(Mp4Reader).Assembly)
        };
    }

    public static FixedOutputBaseline CaptureFixedOutputs()
    {
        var result = new FixedOutputBaseline();
        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
        {
            foreach (var layout in new[]
                     {
                         Mp4WriteMode.Progressive,
                         Mp4WriteMode.FastStart,
                         Mp4WriteMode.Fragmented
                     })
            {
                result.Outputs.Add(CaptureOutput(codec, layout));
            }
        }

        result.Outputs = result.Outputs.OrderBy(value => value.Identity, StringComparer.Ordinal).ToList();
        return result;
    }

    private static List<string> CapturePublicApi(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(value => value.FullName, StringComparer.Ordinal))
        {
            var kind = type.IsEnum ? "enum" :
                type.IsInterface ? "interface" :
                type.IsValueType ? "struct" :
                type.IsSealed ? "sealed class" : "class";
            var baseType = type.BaseType == null || type.BaseType == typeof(object)
                ? string.Empty
                : " : " + TypeName(type.BaseType);
            lines.Add("type " + kind + " " + TypeName(type) + baseType);

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                var constant = field.IsLiteral
                    ? " = " + FormatConstant(field.GetRawConstantValue())
                    : string.Empty;
                lines.Add("  field " + TypeName(field.FieldType) + " " + field.Name + constant);
            }

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(SignatureSortKey, StringComparer.Ordinal))
            {
                lines.Add("  ctor " + type.Name + "(" + Parameters(constructor.GetParameters()) + ")");
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(value => value.Name, StringComparer.Ordinal)
                         .ThenBy(value => Parameters(value.GetIndexParameters()), StringComparer.Ordinal))
            {
                var accessors = (property.GetMethod?.IsPublic == true ? "get;" : string.Empty) +
                                (property.SetMethod?.IsPublic == true ? "set;" : string.Empty);
                var index = property.GetIndexParameters().Length == 0
                    ? property.Name
                    : "this[" + Parameters(property.GetIndexParameters()) + "]";
                lines.Add("  property " + TypeName(property.PropertyType) + " " + index + " {" + accessors + "}");
            }

            foreach (var eventInfo in type.GetEvents(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                lines.Add("  event " + TypeName(eventInfo.EventHandlerType!) + " " + eventInfo.Name);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => !method.IsSpecialName)
                         .OrderBy(SignatureSortKey, StringComparer.Ordinal))
            {
                var staticText = method.IsStatic ? "static " : string.Empty;
                lines.Add("  method " + staticText + TypeName(method.ReturnType) + " " + method.Name +
                          "(" + Parameters(method.GetParameters()) + ")");
            }
        }

        return lines;
    }

    private static string SignatureSortKey(MethodBase method)
    {
        return method.Name + "(" + Parameters(method.GetParameters()) + ")";
    }

    private static string Parameters(IEnumerable<ParameterInfo> parameters)
    {
        return string.Join(", ", parameters.Select(parameter =>
        {
            var modifier = parameter.IsOut ? "out " :
                parameter.ParameterType.IsByRef ? "ref " : string.Empty;
            var type = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            var optional = parameter.HasDefaultValue
                ? " = " + FormatConstant(parameter.DefaultValue)
                : string.Empty;
            return modifier + TypeName(type) + " " + parameter.Name + optional;
        }));
    }

    private static string TypeName(Type type)
    {
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
        if (type.IsGenericParameter) return type.Name;
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var definitionName = type.GetGenericTypeDefinition().FullName!;
        definitionName = definitionName[..definitionName.IndexOf('`')];
        return definitionName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private static string FormatConstant(object? value)
    {
        return value switch
        {
            null => "null",
            string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            char character => "'" + character + "'",
            bool boolean => boolean ? "true" : "false",
            Enum enumValue => Convert.ToInt64(enumValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static FixedOutputCase CaptureOutput(VideoCodec codec, Mp4WriteMode layout)
    {
        using var output = new MemoryStream(2 * 1024 * 1024);
        using (var writer = new Mp4Writer(
                   output,
                   new Mp4WriterOptions
                   {
                       Mode = layout,
                       MaximumFragmentBufferBytes = 4 * 1024 * 1024
                   }))
        {
            writer.SetVideoCodecConfiguration(FixedFixtureMatrix.VideoConfiguration(codec));
            writer.SetAudioCodecConfiguration(FixedFixtureMatrix.AacConfiguration());
            for (var videoIndex = 0; videoIndex < 3; videoIndex++)
            {
                var timestamp = TimeSpan.FromMilliseconds(videoIndex * 40);
                var keyFrame = videoIndex is 0 or 2;
                var first = FixedFixtureMatrix.VideoNal(codec, 257 + videoIndex, keyFrame, 300 + videoIndex * 2);
                var second = FixedFixtureMatrix.VideoNal(codec, 129 + videoIndex, keyFrame, 301 + videoIndex * 2);
                writer.WriteVideoNalUnit(new EncodedVideoNalUnit(
                    FixedFixtureMatrix.AnnexB(first, second),
                    timestamp,
                    timestamp,
                    TimeSpan.FromMilliseconds(40),
                    keyFrame));
                if (videoIndex < 2)
                {
                    var audioTimestamp = TimeSpan.FromMilliseconds(videoIndex * 40);
                    writer.WriteAudioSample(new EncodedAudioSample(
                        FixedFixtureMatrix.AacAccessUnit(191 + videoIndex, 400 + videoIndex),
                        audioTimestamp,
                        audioTimestamp,
                        TimeSpan.FromMilliseconds(40)));
                }
            }

            writer.FinalizeFile();
        }

        var bytes = output.ToArray();
        using var reader = new Mp4Reader(new MemoryStream(bytes, writable: false));
        var video = reader.ReadVideoNalUnits().Select(SerializeVideo).ToList();
        var audio = reader.ReadAudioSamples().Select(SerializeAudio).ToList();
        var events = new List<string>();
        using (var eventReader = new Mp4Reader(new MemoryStream(bytes, writable: false)))
        {
            eventReader.VideoNalUnitRead += (_, sample) => events.Add("video:" + sample.PresentationTimestamp.Ticks);
            eventReader.AacSampleRead += (_, sample) => events.Add("audio:" + sample.PresentationTimestamp.Ticks);
            eventReader.Read();
        }

        return new FixedOutputCase
        {
            Identity = codec.ToString().ToLowerInvariant() + "." + layout.ToString().ToLowerInvariant() + ".annexb-multi-aac",
            Length = bytes.Length,
            Sha256 = Sha256(bytes),
            TopLevelBoxes = ReadTopLevelBoxes(bytes),
            VideoConfiguration = SerializeVideoConfiguration(reader.VideoConfiguration!),
            AudioConfiguration = SerializeAudioConfiguration(reader.AudioConfiguration!),
            VideoSamples = video,
            AudioSamples = audio,
            EventOrder = events
        };
    }

    private static List<BoxSummary> ReadTopLevelBoxes(byte[] bytes)
    {
        var result = new List<BoxSummary>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated top-level MP4 box.");
            var size32 = ReadUInt32(bytes, offset);
            var headerSize = size32 == 1 ? 16 : 8;
            if (bytes.Length - offset < headerSize) throw new InvalidDataException("Truncated extended MP4 box.");
            var size = size32 switch
            {
                0 => bytes.Length - offset,
                1 => checked((int)ReadUInt64(bytes, offset + 8)),
                _ => checked((int)size32)
            };
            if (size < headerSize || size > bytes.Length - offset)
            {
                throw new InvalidDataException("Invalid top-level MP4 box size.");
            }

            var payload = bytes.AsSpan(offset + headerSize, size - headerSize);
            result.Add(new BoxSummary
            {
                Type = Encoding.ASCII.GetString(bytes, offset + 4, 4),
                Size = size,
                PayloadLength = payload.Length,
                PayloadSha256 = Sha256(payload)
            });
            offset += size;
        }

        return result;
    }

    private static string SerializeVideoConfiguration(VideoCodecConfiguration configuration)
    {
        return configuration.Codec + "|" +
               Convert.ToBase64String(configuration.Vps ?? Array.Empty<byte>()) + "|" +
               Convert.ToBase64String(configuration.Sps) + "|" +
               Convert.ToBase64String(configuration.Pps) + "|" +
               configuration.NalLengthSize + "|" +
               configuration.Width + "|" +
               configuration.Height;
    }

    private static string SerializeAudioConfiguration(AacCodecConfiguration configuration)
    {
        return Convert.ToBase64String(configuration.AudioSpecificConfig) + "|" +
               configuration.AudioObjectType + "|" +
               configuration.SampleRate + "|" +
               configuration.ChannelConfiguration;
    }

    private static string SerializeVideo(EncodedVideoNalUnit sample)
    {
        return Convert.ToBase64String(sample.Data) + "|" +
               sample.PresentationTimestamp.Ticks + "|" +
               sample.DecodeTimestamp.Ticks + "|" +
               sample.Duration.Ticks + "|" +
               sample.IsKeyFrame;
    }

    private static string SerializeAudio(EncodedAudioSample sample)
    {
        return Convert.ToBase64String(sample.Data) + "|" +
               sample.PresentationTimestamp.Ticks + "|" +
               sample.DecodeTimestamp.Ticks + "|" +
               sample.Duration.Ticks;
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

    private static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotCore.Mp4.Benchmarks;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(BenchmarkJsonContext.Default.Options)
    {
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            return args[0] switch
            {
                "baseline" => Baseline(args[1..]),
                "capture" => Capture(args[1..]),
                "compare" => Compare(args[1..]),
                "self-test" => SelfTest(),
                _ => Usage()
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static int Baseline(string[] args)
    {
        var root = FindRepositoryRoot();
        var directory = Option(args, "--directory") ??
                        Path.Combine(root, "benchmarks", "DotCore.Mp4.Benchmarks", "Baselines");
        var update = args.Contains("--update", StringComparer.Ordinal);
        var compatibilityPath = Path.Combine(directory, CompatibilityBaselines.CompatibilityFileName);
        var outputsPath = Path.Combine(directory, CompatibilityBaselines.FixedOutputsFileName);
        var compatibility = CompatibilityBaselines.CaptureCompatibility(root);
        var outputs = CompatibilityBaselines.CaptureFixedOutputs();
        if (update)
        {
            Directory.CreateDirectory(directory);
            WriteJson(compatibilityPath, compatibility, BenchmarkJsonContext.Default.CompatibilityBaseline);
            WriteJson(outputsPath, outputs, BenchmarkJsonContext.Default.FixedOutputBaseline);
            Console.WriteLine("Updated compatibility baseline: " + compatibilityPath);
            Console.WriteLine("Updated fixed-output baseline: " + outputsPath);
            return 0;
        }

        var expectedCompatibility = ReadJson(compatibilityPath, BenchmarkJsonContext.Default.CompatibilityBaseline);
        var expectedOutputs = ReadJson(outputsPath, BenchmarkJsonContext.Default.FixedOutputBaseline);
        var errors = new List<string>();
        if (Canonical(expectedCompatibility, BenchmarkJsonContext.Default.CompatibilityBaseline) !=
            Canonical(compatibility, BenchmarkJsonContext.Default.CompatibilityBaseline))
        {
            errors.Add("Public API, target framework, or explicit production package baseline drifted.");
        }

        if (Canonical(expectedOutputs, BenchmarkJsonContext.Default.FixedOutputBaseline) !=
            Canonical(outputs, BenchmarkJsonContext.Default.FixedOutputBaseline))
        {
            errors.Add("Fixed writer output hash, box summary, or public reader round-trip baseline drifted.");
        }

        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        Console.WriteLine("Compatibility and fixed-output baselines match.");
        return 0;
    }

    private static int Capture(string[] args)
    {
        var output = RequiredOption(args, "--output");
        var operations = int.TryParse(Option(args, "--operations"), out var parsed) ? parsed : 3;
        if (operations <= 0) throw new ArgumentOutOfRangeException("--operations", "Operations must be positive.");
        var run = BenchmarkCapture.Capture(FixedFixtureMatrix.Scenarios, operations, Environment.CommandLine);
        BenchmarkRunProvenance.Seal(run, output);
        WriteJson(output, run, BenchmarkJsonContext.Default.BenchmarkRun);
        Console.WriteLine("Captured " + run.Results.Count + " scenarios to " + output);
        return 0;
    }

    private static int Compare(string[] args)
    {
        var baselinePaths = Options(args, "--baseline");
        var candidatePaths = Options(args, "--candidate");
        if (baselinePaths.Count == 0 || candidatePaths.Count == 0)
        {
            throw new ArgumentException("compare requires one or more --baseline and --candidate paths.");
        }

        var baselines = baselinePaths.Select(path => ReadJson(path, BenchmarkJsonContext.Default.BenchmarkRun)).ToArray();
        var candidates = candidatePaths.Select(path => ReadJson(path, BenchmarkJsonContext.Default.BenchmarkRun)).ToArray();
        var report = BenchmarkComparator.Compare(baselines, candidates);
        var reportPath = Option(args, "--output");
        if (reportPath != null) WriteJson(reportPath, report, BenchmarkJsonContext.Default.ComparisonReport);
        foreach (var error in report.Errors) Console.Error.WriteLine(error);
        foreach (var result in report.Results.Where(value => !value.Passed))
        {
            Console.Error.WriteLine(result.Identity + ": " + string.Join("; ", result.Errors));
        }

        Console.WriteLine(report.Passed ? "Benchmark comparison passed." : "Benchmark comparison failed.");
        return report.Passed ? 0 : 1;
    }

    private static int SelfTest()
    {
        var scenarios = FixedFixtureMatrix.Scenarios.Take(2).ToArray();
        var run = BenchmarkCapture.Capture(scenarios, 1, "self-test");
        BenchmarkRunProvenance.Seal(run, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-baseline.json"));
        if (run.Results.Count != 2 || run.Results.Any(result => result.Operations <= 0))
        {
            throw new InvalidOperationException("Capture self-test did not produce nonzero operations.");
        }

        var passingCandidate = Clone(run);
        foreach (var result in passingCandidate.Results.Where(value => value.AllocationGate))
        {
            result.AllocatedBytesPerOperation *= 0.50;
        }
        BenchmarkRunProvenance.Seal(
            passingCandidate,
            Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-candidate.json"));

        var pass = BenchmarkComparator.Compare(new[] { Clone(run) }, new[] { passingCandidate }, minimumRuns: 1);
        if (!pass.Passed) throw new InvalidOperationException("Equal-run comparator self-test failed.");

        var missing = Clone(run);
        missing.Results.RemoveAt(0);
        BenchmarkRunProvenance.Seal(missing, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-missing.json"));
        if (BenchmarkComparator.Compare(new[] { run }, new[] { missing }, minimumRuns: 1).Passed)
        {
            throw new InvalidOperationException("Comparator accepted a missing scenario identity.");
        }

        var allocation = Clone(run);
        allocation.Results[0].AllocationGate = true;
        run.Results[0].AllocationGate = true;
        allocation.Results[0].AllocatedBytesPerOperation = Math.Max(1, run.Results[0].AllocatedBytesPerOperation);
        BenchmarkRunProvenance.Seal(run, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-baseline.json"));
        BenchmarkRunProvenance.Seal(allocation, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-allocation.json"));
        if (BenchmarkComparator.Compare(new[] { run }, new[] { allocation }, minimumRuns: 1).Passed)
        {
            throw new InvalidOperationException("Comparator accepted an allocation reduction below 35%.");
        }

        var throughput = Clone(run);
        throughput.Results[0].Required = true;
        run.Results[0].Required = true;
        throughput.Results[0].OperationsPerSecond = run.Results[0].OperationsPerSecond * 0.89;
        BenchmarkRunProvenance.Seal(run, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-baseline.json"));
        BenchmarkRunProvenance.Seal(throughput, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-throughput.json"));
        if (BenchmarkComparator.Compare(new[] { run }, new[] { throughput }, minimumRuns: 1).Passed)
        {
            throw new InvalidOperationException("Comparator accepted throughput regression beyond 10%.");
        }

        var parameters = Clone(run);
        parameters.Results[0].SampleCount++;
        BenchmarkRunProvenance.Seal(parameters, Path.Combine(Path.GetTempPath(), "dotcore-mp4-self-test-parameters.json"));
        if (BenchmarkComparator.Compare(new[] { run }, new[] { parameters }, minimumRuns: 1).Passed)
        {
            throw new InvalidOperationException("Comparator accepted mismatched scenario parameters.");
        }

        if (BenchmarkComparator.Compare(
                new[] { run, run },
                new[] { throughput, throughput },
                minimumRuns: 2).Passed)
        {
            throw new InvalidOperationException("Comparator accepted duplicate process provenance.");
        }

        Console.WriteLine("Self-test passed: capture, provenance, exact identities/parameters, allocation gate, and throughput gate.");
        return 0;
    }

    private static BenchmarkRun Clone(BenchmarkRun run)
    {
        var json = JsonSerializer.Serialize(run, BenchmarkJsonContext.Default.BenchmarkRun);
        var clone = JsonSerializer.Deserialize(json, BenchmarkJsonContext.Default.BenchmarkRun)!;
        clone.LoadedPath = clone.ResultPath;
        return clone;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotCore.Mp4.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find DotCore.Mp4.sln from the current directory.");
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == name) return args[index + 1];
        }

        return null;
    }

    private static List<string> Options(IReadOnlyList<string> args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == name) values.Add(args[index + 1]);
        }

        return values;
    }

    private static string RequiredOption(IReadOnlyList<string> args, string name)
    {
        return Option(args, name) ?? throw new ArgumentException("Missing required option " + name + ".");
    }

    private static void WriteJson<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, Canonical(value, typeInfo) + Environment.NewLine);
    }

    private static T ReadJson<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var value = JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo) ??
                    throw new InvalidDataException("JSON file deserialized to null: " + path);
        if (value is BenchmarkRun run)
        {
            run.LoadedPath = Path.GetFullPath(path);
        }

        return value;
    }

    private static string Canonical<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Serialize(value, typeInfo);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  baseline [--directory <path>] [--update]");
        Console.Error.WriteLine("  capture --output <path> [--operations <positive-int>]");
        Console.Error.WriteLine("  compare --baseline <path>... --candidate <path>... [--output <path>]");
        Console.Error.WriteLine("  self-test");
        return 2;
    }
}

internal static class BenchmarkCapture
{
    public static BenchmarkRun Capture(
        IReadOnlyList<BenchmarkScenario> scenarios,
        int operations,
        string command)
    {
        var run = new BenchmarkRun
        {
            SourceCommit = Git("rev-parse HEAD"),
            SourceStateSha256 = CaptureSourceStateHash(),
            DirtyWorktree = Git("status --porcelain").Length != 0,
            Command = command,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ??
                        RuntimeInformation.ProcessArchitecture.ToString(),
            CreatedUtc = DateTimeOffset.UtcNow
        };

        foreach (var scenario in scenarios.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            WarmUp(scenario);
            var elapsed = new List<double>();
            var allocated = new List<double>();
            StreamDiagnostics diagnostics = default;
            long gen0Collections = 0;
            long gen1Collections = 0;
            long gen2Collections = 0;
            var benchmark = new Mp4Benchmarks { Scenario = scenario };
            benchmark.GlobalSetup();
            try
            {
                for (var index = 0; index < operations; index++)
                {
                    benchmark.IterationSetup();
                    try
                    {
                        var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
                        var beforeGen0 = GC.CollectionCount(0);
                        var beforeGen1 = GC.CollectionCount(1);
                        var beforeGen2 = GC.CollectionCount(2);
                        var started = Stopwatch.GetTimestamp();
                        _ = benchmark.Execute();
                        var stopped = Stopwatch.GetTimestamp();
                        var afterAllocated = GC.GetTotalAllocatedBytes(precise: true);
                        elapsed.Add((stopped - started) * 1_000_000_000.0 / Stopwatch.Frequency);
                        allocated.Add(afterAllocated - beforeAllocated);
                        gen0Collections += GC.CollectionCount(0) - beforeGen0;
                        gen1Collections += GC.CollectionCount(1) - beforeGen1;
                        gen2Collections += GC.CollectionCount(2) - beforeGen2;
                        diagnostics = GetDiagnostics(benchmark);
                    }
                    finally
                    {
                        benchmark.IterationCleanup();
                    }
                }
            }
            finally
            {
                benchmark.GlobalCleanup();
            }

            var medianNanoseconds = Median(elapsed);
            run.Results.Add(new BenchmarkResult
            {
                Identity = scenario.Id,
                Required = scenario.Required,
                AllocationGate = scenario.AllocationGate,
                LogicalPayloadBytes = scenario.LogicalPayloadBytes,
                SampleCount = scenario.SampleCount,
                NalCount = scenario.NalCount,
                GopLength = scenario.GopLength,
                Operations = operations,
                MedianNanoseconds = medianNanoseconds,
                OperationsPerSecond = 1_000_000_000.0 / medianNanoseconds,
                AllocatedBytesPerOperation = Median(allocated),
                Gen0Collections = gen0Collections,
                Gen1Collections = gen1Collections,
                Gen2Collections = gen2Collections,
                StreamWriteCalls = diagnostics.WriteCalls,
                StreamReadCalls = diagnostics.ReadCalls,
                StreamSeekCalls = diagnostics.SeekCalls,
                StreamSetLengthCalls = diagnostics.SetLengthCalls,
                StreamBytesWritten = diagnostics.BytesWritten,
                OutputGrowthEvents = diagnostics.GrowthEvents
            });
        }

        return run;
    }

    private static void WarmUp(BenchmarkScenario scenario)
    {
        var benchmark = new Mp4Benchmarks { Scenario = scenario };
        benchmark.GlobalSetup();
        benchmark.IterationSetup();
        try
        {
            _ = benchmark.Execute();
        }
        finally
        {
            benchmark.GlobalCleanup();
        }
    }

    private static StreamDiagnostics GetDiagnostics(Mp4Benchmarks benchmark)
    {
        var field = typeof(Mp4Benchmarks).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(benchmark) as CountingMemoryStream)?.Snapshot() ?? default;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) throw new InvalidOperationException("Cannot calculate an empty median.");
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static string Git(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + arguments + " failed: " + error.Trim());
        return output;
    }

    private static string CaptureSourceStateHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Git("diff --no-ext-diff --binary HEAD -- src")));
        var untracked = Git("ls-files --others --exclude-standard -- src")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (var path in untracked)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal static class BenchmarkComparator
{
    public static ComparisonReport Compare(
        IReadOnlyList<BenchmarkRun> baselineRuns,
        IReadOnlyList<BenchmarkRun> candidateRuns,
        int minimumRuns = 3)
    {
        var report = new ComparisonReport
        {
            BaselineRunCount = baselineRuns.Count,
            CandidateRunCount = candidateRuns.Count
        };
        if (baselineRuns.Count < minimumRuns)
        {
            report.Errors.Add("Baseline requires at least " + minimumRuns + " independent process runs.");
        }

        if (candidateRuns.Count < minimumRuns)
        {
            report.Errors.Add("Candidate requires at least " + minimumRuns + " independent process runs.");
        }

        var baseline = ValidateAndGroup("baseline", baselineRuns, report.Errors);
        var candidate = ValidateAndGroup("candidate", candidateRuns, report.Errors);
        ValidateRunCompatibility(baselineRuns, candidateRuns, report.Errors);
        if (!baseline.Keys.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(candidate.Keys.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            report.Errors.Add("Baseline and candidate scenario identity sets do not match exactly.");
        }

        foreach (var identity in baseline.Keys.Intersect(candidate.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            var baselineResults = baseline[identity];
            var candidateResults = candidate[identity];
            var first = baselineResults[0];
            if (!HasSameIdentity(first, candidateResults[0]) ||
                baselineResults.Any(result => result.Operations != first.Operations) ||
                candidateResults.Any(result => result.Operations != first.Operations))
            {
                report.Errors.Add("Baseline and candidate scenario parameters do not match for " + identity + ".");
                continue;
            }

            var baselineAllocation = Median(baselineResults.Select(value => value.AllocatedBytesPerOperation));
            var candidateAllocation = Median(candidateResults.Select(value => value.AllocatedBytesPerOperation));
            var baselineThroughput = Median(baselineResults.Select(value => value.OperationsPerSecond));
            var candidateThroughput = Median(candidateResults.Select(value => value.OperationsPerSecond));
            var item = new ComparisonResult
            {
                Identity = identity,
                Required = first.Required,
                AllocationGate = first.AllocationGate,
                BaselineAllocatedBytes = baselineAllocation,
                CandidateAllocatedBytes = candidateAllocation,
                AllocationReductionPercent = baselineAllocation == 0 ? 0 : (baselineAllocation - candidateAllocation) / baselineAllocation * 100,
                BaselineOperationsPerSecond = baselineThroughput,
                CandidateOperationsPerSecond = candidateThroughput,
                ThroughputChangePercent = baselineThroughput == 0 ? 0 : (candidateThroughput - baselineThroughput) / baselineThroughput * 100
            };
            if (item.AllocationGate && candidateAllocation > baselineAllocation * 0.65)
            {
                item.Errors.Add("Allocated bytes did not decrease by at least 35%.");
            }

            if (item.Required && candidateThroughput < baselineThroughput * 0.90)
            {
                item.Errors.Add("Throughput regressed by more than 10%.");
            }

            item.Passed = item.Errors.Count == 0;
            report.Results.Add(item);
        }

        report.Passed = report.Errors.Count == 0 && report.Results.All(value => value.Passed);
        return report;
    }

    private static Dictionary<string, List<BenchmarkResult>> ValidateAndGroup(
        string side,
        IReadOnlyList<BenchmarkRun> runs,
        ICollection<string> errors)
    {
        var result = new Dictionary<string, List<BenchmarkResult>>(StringComparer.Ordinal);
        IReadOnlyList<string>? expected = null;
        if (runs.Select(run => run.ResultPath).Distinct(StringComparer.Ordinal).Count() != runs.Count)
        {
            errors.Add(side + " runs must have distinct result paths.");
        }

        if (runs.Select(run => run.CanonicalPayloadSha256).Distinct(StringComparer.Ordinal).Count() != runs.Count)
        {
            errors.Add(side + " runs must have distinct provenance hashes.");
        }

        foreach (var run in runs)
        {
            if (!BenchmarkRunProvenance.IsValid(run))
            {
                errors.Add(side + " run has missing or invalid result path/hash provenance: " + run.ResultPath);
            }

            var identities = run.Results.Select(value => value.Identity).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            {
                errors.Add(side + " run contains duplicate scenario identities.");
            }

            if (expected != null && !expected.SequenceEqual(identities, StringComparer.Ordinal))
            {
                errors.Add(side + " runs do not contain identical scenario identity sets.");
            }

            expected ??= identities;
            foreach (var item in run.Results)
            {
                if (item.Operations <= 0 ||
                    !double.IsFinite(item.AllocatedBytesPerOperation) ||
                    !double.IsFinite(item.OperationsPerSecond) ||
                    item.OperationsPerSecond <= 0)
                {
                    errors.Add(side + " result is invalid for " + item.Identity + ".");
                }

                if (!result.TryGetValue(item.Identity, out var values))
                {
                    values = new List<BenchmarkResult>();
                    result.Add(item.Identity, values);
                }
                else if (values.Count != 0 && !HasSameIdentity(values[0], item))
                {
                    errors.Add(side + " result has mismatched scenario parameters for " + item.Identity + ".");
                }

                values.Add(item);
            }
        }

        if (runs.Count == 0) errors.Add(side + " has no runs.");
        return result;
    }

    private static void ValidateRunCompatibility(
        IReadOnlyList<BenchmarkRun> baselineRuns,
        IReadOnlyList<BenchmarkRun> candidateRuns,
        ICollection<string> errors)
    {
        var runs = baselineRuns.Concat(candidateRuns).ToArray();
        if (runs.Length == 0) return;
        var expected = runs[0];
        foreach (var run in runs.Skip(1))
        {
            if (run.HarnessVersion != expected.HarnessVersion ||
                run.SourceCommit != expected.SourceCommit ||
                run.Runtime != expected.Runtime ||
                run.OperatingSystem != expected.OperatingSystem ||
                run.Processor != expected.Processor)
            {
                errors.Add("Benchmark runs do not share the same harness, source commit, runtime, OS, and processor identity.");
                return;
            }
        }
    }

    private static bool HasSameIdentity(BenchmarkResult left, BenchmarkResult right)
    {
        return left.Identity == right.Identity &&
               left.Required == right.Required &&
               left.AllocationGate == right.AllocationGate &&
               left.LogicalPayloadBytes == right.LogicalPayloadBytes &&
               left.SampleCount == right.SampleCount &&
               left.NalCount == right.NalCount &&
               left.GopLength == right.GopLength;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}

internal static class BenchmarkRunProvenance
{
    public static void Seal(BenchmarkRun run, string path)
    {
        run.ResultPath = Path.GetFullPath(path);
        run.LoadedPath = run.ResultPath;
        run.CanonicalPayloadSha256 = string.Empty;
        run.CanonicalPayloadSha256 = ComputeHash(run);
    }

    public static bool IsValid(BenchmarkRun run)
    {
        if (string.IsNullOrWhiteSpace(run.ResultPath) ||
            string.IsNullOrWhiteSpace(run.CanonicalPayloadSha256) ||
            string.IsNullOrWhiteSpace(run.SourceStateSha256) ||
            !string.Equals(
                Path.GetFullPath(run.LoadedPath),
                Path.GetFullPath(run.ResultPath),
                StringComparison.Ordinal))
        {
            return false;
        }

        var expected = run.CanonicalPayloadSha256;
        run.CanonicalPayloadSha256 = string.Empty;
        try
        {
            return string.Equals(expected, ComputeHash(run), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            run.CanonicalPayloadSha256 = expected;
        }
    }

    private static string ComputeHash(BenchmarkRun run)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(run, BenchmarkJsonContext.Default.BenchmarkRun);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

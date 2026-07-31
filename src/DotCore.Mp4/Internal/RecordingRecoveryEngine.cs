using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DotCore.Mp4;

internal static class RecordingRecoveryEngine
{
    public static Mp4RecordingRecoveryResult Recover(
        RecordingPaths paths,
        Mp4RecordingRecoveryOptions options)
    {
        if (File.Exists(paths.JournalPath))
        {
            return RecoverWithJournal(paths, options);
        }

        return RecoverWithoutJournal(paths, options, null, new List<string>());
    }

    public static async Task<Mp4RecordingRecoveryResult> RecoverAsync(
        RecordingPaths paths,
        Mp4RecordingRecoveryOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(paths.JournalPath))
        {
            return await RecoverWithJournalAsync(paths, options, cancellationToken).ConfigureAwait(false);
        }

        return await RecoverWithoutJournalAsync(paths, options, null, new List<string>(), cancellationToken).ConfigureAwait(false);
    }

    private static Mp4RecordingRecoveryResult RecoverWithJournal(
        RecordingPaths paths,
        Mp4RecordingRecoveryOptions options)
    {
        var warnings = new List<string>();
        RecordingJournalSnapshot? snapshot = null;
        var completed = false;
        var cleanupAllowed = false;
        FileStream journal;
        try
        {
            journal = OpenJournal(paths.JournalPath);
        }
        catch (IOException)
        {
            warnings.Add("The recording journal is in use by an active writer or recovery operation.");
            return NoMedia(warnings);
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add("The recording journal could not be exclusively opened for recovery.");
            return NoMedia(warnings);
        }

        try
        {
            using (journal)
            {
                snapshot = RecordingJournal.Read(journal, paths.TargetIdentity());
                AddWarnings(warnings, snapshot.Warnings);
                if (snapshot.CompletedTargetHash != null)
                {
                    if (!IsCompletedTargetValid(paths.TargetPath, snapshot.CompletedTargetHash))
                    {
                        warnings.Add("The completed journal marker does not match a strict-valid target.");
                        return NoMedia(warnings);
                    }

                    completed = true;
                    cleanupAllowed = true;
                }
                else if (File.Exists(paths.TargetPath))
                {
                    warnings.Add("The target already exists and was not replaced.");
                    return NoMedia(warnings);
                }
                else if (snapshot.IsTrusted)
                {
                    var status = TryRecoverExact(paths, snapshot, journal, warnings);
                    completed = status != ExactStatus.None;
                    cleanupAllowed = status == ExactStatus.DeliveredWithMarker;
                }
            }
        }
        catch (Mp4FormatException error)
        {
            warnings.Add("The recording journal is unusable: " + error.Message);
        }

        if (snapshot != null && completed)
        {
            return CompleteExact(paths, snapshot, warnings, cleanupAllowed);
        }

        if (snapshot != null)
        {
            var knownCapture = paths.CapturePath(snapshot.Header.CaptureId);
            return RecoverWithoutJournal(paths, options, File.Exists(knownCapture) ? knownCapture : null, warnings);
        }

        return RecoverWithoutJournal(paths, options, null, warnings);
    }

    private static async Task<Mp4RecordingRecoveryResult> RecoverWithJournalAsync(
        RecordingPaths paths,
        Mp4RecordingRecoveryOptions options,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        RecordingJournalSnapshot? snapshot = null;
        var completed = false;
        var cleanupAllowed = false;
        FileStream journal;
        try
        {
            journal = OpenJournal(paths.JournalPath);
        }
        catch (IOException)
        {
            warnings.Add("The recording journal is in use by an active writer or recovery operation.");
            return NoMedia(warnings);
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add("The recording journal could not be exclusively opened for recovery.");
            return NoMedia(warnings);
        }

        try
        {
            using (journal)
            {
                snapshot = await RecordingJournal
                    .ReadAsync(journal, paths.TargetIdentity(), cancellationToken)
                    .ConfigureAwait(false);
                AddWarnings(warnings, snapshot.Warnings);
                if (snapshot.CompletedTargetHash != null)
                {
                    if (!await IsCompletedTargetValidAsync(paths.TargetPath, snapshot.CompletedTargetHash, cancellationToken).ConfigureAwait(false))
                    {
                        warnings.Add("The completed journal marker does not match a strict-valid target.");
                        return NoMedia(warnings);
                    }

                    completed = true;
                    cleanupAllowed = true;
                }
                else if (File.Exists(paths.TargetPath))
                {
                    warnings.Add("The target already exists and was not replaced.");
                    return NoMedia(warnings);
                }
                else if (snapshot.IsTrusted)
                {
                    var status = await TryRecoverExactAsync(paths, snapshot, journal, warnings, cancellationToken).ConfigureAwait(false);
                    completed = status != ExactStatus.None;
                    cleanupAllowed = status == ExactStatus.DeliveredWithMarker;
                }
            }
        }
        catch (Mp4FormatException error)
        {
            warnings.Add("The recording journal is unusable: " + error.Message);
        }

        if (snapshot != null && completed)
        {
            return CompleteExact(paths, snapshot, warnings, cleanupAllowed);
        }

        if (snapshot != null)
        {
            var knownCapture = paths.CapturePath(snapshot.Header.CaptureId);
            return await RecoverWithoutJournalAsync(
                    paths,
                    options,
                    File.Exists(knownCapture) ? knownCapture : null,
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await RecoverWithoutJournalAsync(paths, options, null, warnings, cancellationToken).ConfigureAwait(false);
    }

    private static ExactStatus TryRecoverExact(
        RecordingPaths paths,
        RecordingJournalSnapshot snapshot,
        Stream journal,
        IList<string> warnings)
    {
        if (snapshot.Samples.Count == 0)
        {
            warnings.Add("The journal contains no committed media samples.");
            return ExactStatus.None;
        }

        var capturePath = paths.CapturePath(snapshot.Header.CaptureId);
        if (!ValidateCapture(snapshot, capturePath, warnings)) return ExactStatus.None;

        var stagingPath = paths.CreateStagingPath();
        var delivered = false;
        try
        {
            RebuildExact(capturePath, stagingPath, snapshot);
            ValidateStaging(stagingPath);
            var targetHash = HashFile(stagingPath);
            Deliver(paths, stagingPath);
            delivered = true;
            var markerWritten = true;
            try
            {
                RecordingJournal.AppendCompleted(journal, targetHash);
                FlushJournal(journal);
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is NotSupportedException)
            {
                warnings.Add("The delivered target could not be marked completed; stale artifacts were retained.");
                markerWritten = false;
            }

            return markerWritten ? ExactStatus.DeliveredWithMarker : ExactStatus.DeliveredWithoutMarker;
        }
        catch (Mp4FormatException error)
        {
            if (!delivered) TryDelete(stagingPath, warnings);
            warnings.Add("Exact recovery was rejected: " + error.Message);
            return ExactStatus.None;
        }
        catch (InvalidOperationException error)
        {
            if (!delivered) TryDelete(stagingPath, warnings);
            warnings.Add("Exact recovery was rejected: " + error.Message);
            return ExactStatus.None;
        }
    }

    private static async Task<ExactStatus> TryRecoverExactAsync(
        RecordingPaths paths,
        RecordingJournalSnapshot snapshot,
        Stream journal,
        IList<string> warnings,
        CancellationToken cancellationToken)
    {
        if (snapshot.Samples.Count == 0)
        {
            warnings.Add("The journal contains no committed media samples.");
            return ExactStatus.None;
        }

        var capturePath = paths.CapturePath(snapshot.Header.CaptureId);
        if (!ValidateCapture(snapshot, capturePath, warnings)) return ExactStatus.None;

        var stagingPath = paths.CreateStagingPath();
        var markerWritten = true;
        var delivered = false;
        try
        {
            await RebuildExactAsync(capturePath, stagingPath, snapshot, cancellationToken).ConfigureAwait(false);
            await ValidateStagingAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            var targetHash = await HashFileAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            Deliver(paths, stagingPath);
            delivered = true;
            try
            {
                await RecordingJournal
                    .AppendCompletedAsync(journal, targetHash, cancellationToken)
                    .ConfigureAwait(false);
                await journal.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is NotSupportedException)
            {
                warnings.Add("The delivered target could not be marked completed; stale artifacts were retained.");
                markerWritten = false;
            }

            return markerWritten ? ExactStatus.DeliveredWithMarker : ExactStatus.DeliveredWithoutMarker;
        }
        catch (Mp4FormatException error)
        {
            if (!delivered) TryDelete(stagingPath, warnings);
            warnings.Add("Exact recovery was rejected: " + error.Message);
            return ExactStatus.None;
        }
        catch (InvalidOperationException error)
        {
            if (!delivered) TryDelete(stagingPath, warnings);
            warnings.Add("Exact recovery was rejected: " + error.Message);
            return ExactStatus.None;
        }
    }

    private static Mp4RecordingRecoveryResult CompleteExact(
        RecordingPaths paths,
        RecordingJournalSnapshot snapshot,
        IList<string> warnings,
        bool cleanupAllowed)
    {
        if (cleanupAllowed)
        {
            TryDelete(paths.JournalPath, warnings);
            TryDelete(paths.CapturePath(snapshot.Header.CaptureId), warnings);
            TryDeleteStagingArtifacts(paths, warnings);
        }

        return new Mp4RecordingRecoveryResult(Mp4RecordingRecoveryTier.Exact, paths.TargetPath, CopyWarnings(warnings));
    }

    private static Mp4RecordingRecoveryResult RecoverWithoutJournal(
        RecordingPaths paths,
        Mp4RecordingRecoveryOptions options,
        string? knownCapturePath,
        IList<string> warnings)
    {
        if (File.Exists(paths.TargetPath))
        {
            warnings.Add("The target already exists and was not replaced.");
            return NoMedia(warnings);
        }

        var capturePath = FindSingleCapture(paths, knownCapturePath, warnings);
        if (capturePath == null) return NoMedia(warnings);

        byte[] data;
        try
        {
            data = ReadAllBytes(capturePath);
        }
        catch (Mp4FormatException error)
        {
            warnings.Add(error.Message);
            return NoMedia(warnings);
        }

        var structuralEnd = FindLastSemanticallyValidFragmentEnd(data, FindStructuralFragmentEnds(data));
        if (structuralEnd != 0)
        {
            var staging = paths.CreateStagingPath();
            try
            {
                WritePrefix(staging, data, structuralEnd);
                ValidateStaging(staging);
                Deliver(paths, staging);
                warnings.Add("Recovered complete fragmented prefixes without a usable journal.");
                return new Mp4RecordingRecoveryResult(
                    Mp4RecordingRecoveryTier.Structural,
                    paths.TargetPath,
                    CopyWarnings(warnings));
            }
            catch (Mp4FormatException error)
            {
                TryDelete(staging, warnings);
                warnings.Add("Structural recovery was rejected: " + error.Message);
            }
            catch (InvalidOperationException error)
            {
                TryDelete(staging, warnings);
                warnings.Add("Structural recovery was rejected: " + error.Message);
            }
            catch (IOException)
            {
                TryDelete(staging, warnings);
                warnings.Add("The target could not be safely delivered because another operation created or retained it.");
                return NoMedia(warnings);
            }
        }

        if (options.EnableHeuristicRecovery && TryRecoverHeuristically(paths, data, warnings))
        {
            return new Mp4RecordingRecoveryResult(
                Mp4RecordingRecoveryTier.Heuristic,
                paths.TargetPath,
                CopyWarnings(warnings));
        }

        if (options.EnableHeuristicRecovery)
        {
            warnings.Add("No unambiguous video-only heuristic recovery input was found.");
        }

        return NoMedia(warnings);
    }

    private static async Task<Mp4RecordingRecoveryResult> RecoverWithoutJournalAsync(
        RecordingPaths paths,
        Mp4RecordingRecoveryOptions options,
        string? knownCapturePath,
        IList<string> warnings,
        CancellationToken cancellationToken)
    {
        if (File.Exists(paths.TargetPath))
        {
            warnings.Add("The target already exists and was not replaced.");
            return NoMedia(warnings);
        }

        var capturePath = FindSingleCapture(paths, knownCapturePath, warnings);
        if (capturePath == null) return NoMedia(warnings);

        byte[] data;
        try
        {
            data = await ReadAllBytesAsync(capturePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Mp4FormatException error)
        {
            warnings.Add(error.Message);
            return NoMedia(warnings);
        }

        var structuralEnd = FindLastSemanticallyValidFragmentEnd(data, FindStructuralFragmentEnds(data));
        if (structuralEnd != 0)
        {
            var staging = paths.CreateStagingPath();
            try
            {
                await WritePrefixAsync(staging, data, structuralEnd, cancellationToken).ConfigureAwait(false);
                await ValidateStagingAsync(staging, cancellationToken).ConfigureAwait(false);
                Deliver(paths, staging);
                warnings.Add("Recovered complete fragmented prefixes without a usable journal.");
                return new Mp4RecordingRecoveryResult(
                    Mp4RecordingRecoveryTier.Structural,
                    paths.TargetPath,
                    CopyWarnings(warnings));
            }
            catch (Mp4FormatException error)
            {
                TryDelete(staging, warnings);
                warnings.Add("Structural recovery was rejected: " + error.Message);
            }
            catch (InvalidOperationException error)
            {
                TryDelete(staging, warnings);
                warnings.Add("Structural recovery was rejected: " + error.Message);
            }
            catch (IOException)
            {
                TryDelete(staging, warnings);
                warnings.Add("The target could not be safely delivered because another operation created or retained it.");
                return NoMedia(warnings);
            }
        }

        if (options.EnableHeuristicRecovery &&
            await TryRecoverHeuristicallyAsync(paths, data, warnings, cancellationToken).ConfigureAwait(false))
        {
            return new Mp4RecordingRecoveryResult(
                Mp4RecordingRecoveryTier.Heuristic,
                paths.TargetPath,
                CopyWarnings(warnings));
        }

        if (options.EnableHeuristicRecovery)
        {
            warnings.Add("No unambiguous video-only heuristic recovery input was found.");
        }

        return NoMedia(warnings);
    }

    private static bool ValidateCapture(
        RecordingJournalSnapshot snapshot,
        string capturePath,
        IList<string> warnings)
    {
        if (!File.Exists(capturePath))
        {
            warnings.Add("The journal-referenced capture file is missing.");
            return false;
        }

        var length = new FileInfo(capturePath).Length;
        if (length < RecordingCapture.PayloadOffset || length > Mp4Reader.MaximumInputBytes)
        {
            warnings.Add("The journal-referenced capture length is outside the supported range.");
            return false;
        }

        using (var capture = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!RecordingCapture.HasIdentity(capture, snapshot.Header.CaptureId))
            {
                warnings.Add("The journal-referenced capture identity does not match.");
                return false;
            }
        }

        var boundary = snapshot.Samples[snapshot.Samples.Count - 1].Boundary;
        if (boundary > length)
        {
            warnings.Add("The capture is shorter than the last journal-confirmed boundary.");
            return false;
        }

        return true;
    }

    private static void RebuildExact(
        string capturePath,
        string stagingPath,
        RecordingJournalSnapshot snapshot)
    {
        using (var capture = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var staging = OpenStaging(stagingPath))
        using (var writer = new Mp4Writer(staging, CreateWriterOptions(snapshot.Header.Mode), leaveOpen: true))
        {
            ConfigureWriter(writer, snapshot);
            foreach (var sample in snapshot.Samples)
            {
                var data = ReadSample(capture, sample);
                WriteSample(writer, snapshot, sample, data);
            }

            writer.FinalizeFile();
            staging.Flush(true);
        }
    }

    private static async Task RebuildExactAsync(
        string capturePath,
        string stagingPath,
        RecordingJournalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using (var capture = OpenRead(capturePath))
        using (var staging = OpenStaging(stagingPath))
        using (var writer = await Mp4Writer
            .CreateAsync(staging, CreateWriterOptions(snapshot.Header.Mode), leaveOpen: true, cancellationToken)
            .ConfigureAwait(false))
        {
            ConfigureWriter(writer, snapshot);
            foreach (var sample in snapshot.Samples)
            {
                var data = await ReadSampleAsync(capture, sample, cancellationToken).ConfigureAwait(false);
                await WriteSampleAsync(writer, snapshot, sample, data, cancellationToken).ConfigureAwait(false);
            }

            await writer.FinalizeFileAsync(cancellationToken).ConfigureAwait(false);
            await staging.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ConfigureWriter(Mp4Writer writer, RecordingJournalSnapshot snapshot)
    {
        if (snapshot.VideoConfiguration != null) writer.SetVideoCodecConfiguration(snapshot.VideoConfiguration);
        if (snapshot.AudioConfiguration != null) writer.SetAudioCodecConfiguration(snapshot.AudioConfiguration);
    }

    private static void WriteSample(
        Mp4Writer writer,
        RecordingJournalSnapshot snapshot,
        RecordingJournalSample sample,
        byte[] data)
    {
        if (sample.Kind == RecordingSampleKind.Video)
        {
            var configuration = snapshot.VideoConfiguration ?? throw new Mp4FormatException("Video configuration is missing.");
            writer.WriteVideoNalUnit(
                new EncodedVideoNalUnit(
                    RecordingCapture.DecodeVideo(data, configuration.NalLengthSize),
                    MediaTime.FromTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale),
                    MediaTime.FromTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale),
                    MediaTime.FromTicks(sample.Duration, MediaTime.DefaultTrackTimescale),
                    sample.IsKeyFrame));
            return;
        }

        writer.WriteAudioSample(
            new EncodedAudioSample(
                data,
                MediaTime.FromTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale),
                MediaTime.FromTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale),
                MediaTime.FromTicks(sample.Duration, MediaTime.DefaultTrackTimescale)));
    }

    private static async Task WriteSampleAsync(
        Mp4Writer writer,
        RecordingJournalSnapshot snapshot,
        RecordingJournalSample sample,
        byte[] data,
        CancellationToken cancellationToken)
    {
        if (sample.Kind == RecordingSampleKind.Video)
        {
            var configuration = snapshot.VideoConfiguration ?? throw new Mp4FormatException("Video configuration is missing.");
            await writer
                .WriteVideoNalUnitAsync(
                    new EncodedVideoNalUnit(
                        RecordingCapture.DecodeVideo(data, configuration.NalLengthSize),
                        MediaTime.FromTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale),
                        MediaTime.FromTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale),
                        MediaTime.FromTicks(sample.Duration, MediaTime.DefaultTrackTimescale),
                        sample.IsKeyFrame),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await writer
            .WriteAudioSampleAsync(
                new EncodedAudioSample(
                    data,
                    MediaTime.FromTicks(sample.PresentationTimestamp, MediaTime.DefaultTrackTimescale),
                    MediaTime.FromTicks(sample.DecodeTimestamp, MediaTime.DefaultTrackTimescale),
                    MediaTime.FromTicks(sample.Duration, MediaTime.DefaultTrackTimescale)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] ReadSample(Stream capture, RecordingJournalSample sample)
    {
        capture.Position = sample.Offset;
        var data = new byte[sample.Size];
        ReadExactly(capture, data, 0, data.Length);
        return data;
    }

    private static async Task<byte[]> ReadSampleAsync(
        Stream capture,
        RecordingJournalSample sample,
        CancellationToken cancellationToken)
    {
        capture.Position = sample.Offset;
        var data = new byte[sample.Size];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = await capture
                .ReadAsync(data, offset, data.Length - offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new Mp4FormatException("The capture ended before a journal-confirmed sample was read.");
            offset += read;
        }

        return data;
    }

    private static bool TryRecoverHeuristically(RecordingPaths paths, byte[] data, IList<string> warnings)
    {
        HeuristicVideo? video;
        bool discardedTerminalNal;
        if (!TryFindHeuristicVideo(data, out video, out discardedTerminalNal) || video == null) return false;
        var staging = paths.CreateStagingPath();
        try
        {
            using (var output = OpenStaging(staging))
            using (var writer = new Mp4Writer(output, new Mp4WriterOptions(), leaveOpen: true))
            {
                writer.SetVideoCodecConfiguration(video.Configuration);
                WriteHeuristicSamples(writer, video);
                writer.FinalizeFile();
                output.Flush(true);
            }

            ValidateStaging(staging);
            Deliver(paths, staging);
            if (discardedTerminalNal)
            {
                warnings.Add("Heuristic recovery discarded an incomplete terminal video NAL.");
            }

            warnings.Add("Heuristic recovery generated synthetic video-only timing and omitted all AAC media.");
            return true;
        }
        catch (Mp4FormatException)
        {
            TryDelete(staging, warnings);
            return false;
        }
        catch (InvalidOperationException)
        {
            TryDelete(staging, warnings);
            return false;
        }
        catch (IOException)
        {
            TryDelete(staging, warnings);
            warnings.Add("The heuristic staging output could not be safely delivered.");
            return false;
        }
    }

    private static async Task<bool> TryRecoverHeuristicallyAsync(
        RecordingPaths paths,
        byte[] data,
        IList<string> warnings,
        CancellationToken cancellationToken)
    {
        HeuristicVideo? video;
        bool discardedTerminalNal;
        if (!TryFindHeuristicVideo(data, out video, out discardedTerminalNal) || video == null) return false;
        var staging = paths.CreateStagingPath();
        try
        {
            using (var output = OpenStaging(staging))
            using (var writer = await Mp4Writer
                .CreateAsync(output, new Mp4WriterOptions(), leaveOpen: true, cancellationToken)
                .ConfigureAwait(false))
            {
                writer.SetVideoCodecConfiguration(video.Configuration);
                await WriteHeuristicSamplesAsync(writer, video, cancellationToken).ConfigureAwait(false);
                await writer.FinalizeFileAsync(cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await ValidateStagingAsync(staging, cancellationToken).ConfigureAwait(false);
            Deliver(paths, staging);
            if (discardedTerminalNal)
            {
                warnings.Add("Heuristic recovery discarded an incomplete terminal video NAL.");
            }

            warnings.Add("Heuristic recovery generated synthetic video-only timing and omitted all AAC media.");
            return true;
        }
        catch (Mp4FormatException)
        {
            TryDelete(staging, warnings);
            return false;
        }
        catch (InvalidOperationException)
        {
            TryDelete(staging, warnings);
            return false;
        }
        catch (IOException)
        {
            TryDelete(staging, warnings);
            warnings.Add("The heuristic staging output could not be safely delivered.");
            return false;
        }
    }

    private static void WriteHeuristicSamples(Mp4Writer writer, HeuristicVideo video)
    {
        var timestamp = TimeSpan.Zero;
        var duration = TimeSpan.FromMilliseconds(1);
        foreach (var nal in video.MediaNals)
        {
            writer.WriteVideoNalUnit(new EncodedVideoNalUnit(nal, timestamp, timestamp, duration, IsKeyFrame(video.Codec, nal)));
            timestamp = checked(timestamp + duration);
        }
    }

    private static async Task WriteHeuristicSamplesAsync(
        Mp4Writer writer,
        HeuristicVideo video,
        CancellationToken cancellationToken)
    {
        var timestamp = TimeSpan.Zero;
        var duration = TimeSpan.FromMilliseconds(1);
        foreach (var nal in video.MediaNals)
        {
            await writer
                .WriteVideoNalUnitAsync(
                    new EncodedVideoNalUnit(nal, timestamp, timestamp, duration, IsKeyFrame(video.Codec, nal)),
                    cancellationToken)
                .ConfigureAwait(false);
            timestamp = checked(timestamp + duration);
        }
    }

    private static bool TryFindHeuristicVideo(
        byte[] data,
        out HeuristicVideo? video,
        out bool discardedTerminalNal)
    {
        video = null;
        discardedTerminalNal = false;
        int payloadStart;
        int payloadEnd;
        if (!TryFindHeuristicMdatPayload(data, out payloadStart, out payloadEnd)) return false;

        HeuristicVideo? candidate = null;
        var candidateDiscardedTerminalNal = false;
        for (var lengthSize = 1; lengthSize <= 4; lengthSize++)
        {
            IList<byte[]> nals;
            bool discarded;
            if (!TryParseLengthPrefixedNals(data, payloadStart, payloadEnd, lengthSize, out nals, out discarded)) continue;
            var parsed = CreateHeuristicVideo(lengthSize, nals);
            if (parsed == null) continue;
            if (candidate != null) return false;
            candidate = parsed;
            candidateDiscardedTerminalNal = discarded;
        }

        video = candidate;
        discardedTerminalNal = candidateDiscardedTerminalNal;
        return video != null;
    }

    private static HeuristicVideo? CreateHeuristicVideo(int lengthSize, IList<byte[]> nals)
    {
        var h264 = TryCreateHeuristicVideo(VideoCodec.H264, lengthSize, nals);
        var h265 = TryCreateHeuristicVideo(VideoCodec.H265, lengthSize, nals);
        return h264 != null && h265 != null ? null : h264 ?? h265;
    }

    private static HeuristicVideo? TryCreateHeuristicVideo(
        VideoCodec codec,
        int lengthSize,
        IList<byte[]> nals)
    {
        var media = new List<byte[]>();
        byte[]? vps = null;
        byte[]? sps = null;
        byte[]? pps = null;
        foreach (var nal in nals)
        {
            if (!IsCompatibleVideoNal(codec, nal)) return null;

            var type = GetNalType(codec, nal);
            if (codec == VideoCodec.H264 && type == 7)
            {
                if (sps != null) return null;
                sps = nal;
                continue;
            }

            if (codec == VideoCodec.H264 && type == 8)
            {
                if (pps != null) return null;
                pps = nal;
                continue;
            }

            if (codec == VideoCodec.H265 && type == 32)
            {
                if (vps != null) return null;
                vps = nal;
                continue;
            }

            if (codec == VideoCodec.H265 && type == 33)
            {
                if (sps != null) return null;
                sps = nal;
                continue;
            }

            if (codec == VideoCodec.H265 && type == 34)
            {
                if (pps != null) return null;
                pps = nal;
                continue;
            }

            media.Add(nal);
        }

        if (media.Count == 0 || !IsKeyFrame(codec, media[0])) return null;
        if (codec == VideoCodec.H264 && sps != null && pps != null)
        {
            return new HeuristicVideo(
                codec,
                new VideoCodecConfiguration(codec, sps, pps, lengthSize),
                media);
        }

        if (codec == VideoCodec.H265 && vps != null && sps != null && pps != null)
        {
            return new HeuristicVideo(
                codec,
                new VideoCodecConfiguration(codec, vps, sps, pps, lengthSize),
                media);
        }

        return null;
    }

    private static bool IsCompatibleVideoNal(VideoCodec codec, byte[] nal)
    {
        if (nal.Length == 0 || (nal[0] & 0x80) != 0) return false;
        if (codec == VideoCodec.H264)
        {
            var type = GetNalType(codec, nal);
            return type >= 1 && type <= 23;
        }

        if (nal.Length < 2 || (nal[1] & 0x07) == 0 || (nal[1] & 0xf8) != 0) return false;
        return GetNalType(codec, nal) <= 40;
    }

    private static int GetNalType(VideoCodec codec, byte[] nal)
    {
        return codec == VideoCodec.H264 ? nal[0] & 0x1f : (nal[0] >> 1) & 0x3f;
    }

    private static bool IsKeyFrame(VideoCodec codec, byte[] nal)
    {
        var type = codec == VideoCodec.H264 ? nal[0] & 0x1f : (nal[0] >> 1) & 0x3f;
        return codec == VideoCodec.H264 ? type == 5 : type == 19 || type == 20 || type == 21;
    }

    private static bool TryParseLengthPrefixedNals(
        byte[] data,
        int start,
        int end,
        int lengthSize,
        out IList<byte[]> nals,
        out bool discardedTerminalNal)
    {
        nals = new List<byte[]>();
        discardedTerminalNal = false;
        var offset = start;
        while (offset < end)
        {
            if (end - offset < lengthSize)
            {
                discardedTerminalNal = true;
                return nals.Count != 0;
            }

            uint length = 0;
            for (var index = 0; index < lengthSize; index++) length = (length << 8) | data[offset + index];
            offset += lengthSize;
            if (length == 0 || length > int.MaxValue) return false;
            if (length > end - offset)
            {
                discardedTerminalNal = true;
                return nals.Count != 0;
            }

            if (nals.Count >= Mp4Reader.MaximumSampleCount) return false;
            var nal = new byte[(int)length];
            Buffer.BlockCopy(data, offset, nal, 0, nal.Length);
            nals.Add(nal);
            offset += nal.Length;
        }

        return nals.Count != 0;
    }

    private static bool TryFindHeuristicMdatPayload(byte[] data, out int start, out int end)
    {
        start = 0;
        end = 0;
        var offset = 0;
        var foundFileType = false;
        var foundMedia = false;
        while (offset < data.Length)
        {
            Box box;
            if (!TryReadBox(data, offset, out box)) return false;
            if (box.Type == "ftyp")
            {
                if (foundFileType || offset != 0 || box.End - box.PayloadStart < 8) return false;
                foundFileType = true;
            }
            else if (box.Type == "mdat")
            {
                if (!foundFileType || foundMedia || !IsOpenEndedMdat(data, box)) return false;
                start = box.PayloadStart;
                end = box.End;
                foundMedia = true;
            }
            else if (box.Type != "free" && box.Type != "skip" && box.Type != "wide")
            {
                return false;
            }

            offset = box.End;
        }

        return foundFileType && foundMedia && start < end;
    }

    private static bool IsOpenEndedMdat(byte[] data, Box box)
    {
        var size32 = ReadU32(data, box.Start);
        return size32 == 0 || (size32 == 1 && ReadU64(data, box.Start + 8) == 0);
    }

    private static IList<int> FindStructuralFragmentEnds(byte[] data)
    {
        var ends = new List<int>();
        var offset = 0;
        var prefixEnd = 0;
        var hasFileType = false;
        var hasMovie = false;
        var topLevelBoxCount = 0;
        while (offset < data.Length)
        {
            if (++topLevelBoxCount > 100_000) return ends;
            Box box;
            if (!TryReadBox(data, offset, out box)) break;
            if (box.Type == "moof")
            {
                prefixEnd = offset;
                break;
            }

            if (box.Type == "ftyp") hasFileType = true;
            if (box.Type == "moov") hasMovie = true;
            offset = box.End;
        }

        if (!hasFileType || !hasMovie || prefixEnd == 0) return ends;
        offset = prefixEnd;
        var fragmentCount = 0;
        while (offset < data.Length)
        {
            if (++fragmentCount > Mp4Reader.MaximumFragmentCount)
            {
                ends.Clear();
                return ends;
            }

            Box moof;
            if (!TryReadBox(data, offset, out moof) || moof.Type != "moof") break;
            Box mdat;
            if (!TryReadBox(data, moof.End, out mdat) || mdat.Type != "mdat" || mdat.PayloadStart == mdat.End) break;
            ends.Add(mdat.End);
            offset = mdat.End;
        }

        return ends;
    }

    private static int FindLastSemanticallyValidFragmentEnd(byte[] data, IList<int> ends)
    {
        var lower = 0;
        var upper = ends.Count - 1;
        var valid = -1;
        while (lower <= upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (IsSemanticallyValidFragmentPrefix(data, ends[middle]))
            {
                valid = middle;
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }

        return valid < 0 ? 0 : ends[valid];
    }

    private static bool IsSemanticallyValidFragmentPrefix(byte[] data, int length)
    {
        try
        {
            using (var input = new MemoryStream(data, 0, length, writable: false, publiclyVisible: true))
            using (var reader = new Mp4Reader(input))
            {
                return HasMedia(reader);
            }
        }
        catch (Mp4FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadBox(byte[] data, int offset, out Box box)
    {
        box = default(Box);
        if (offset < 0 || data.Length - offset < 8) return false;
        var size32 = ReadU32(data, offset);
        var type = FourCc(data, offset + 4);
        var header = 8;
        long size;
        if (size32 == 1)
        {
            if (data.Length - offset < 16) return false;
            var size64 = ReadU64(data, offset + 8);
            header = 16;
            if (size64 == 0 && type == "mdat")
            {
                size = data.Length - offset;
            }
            else
            {
                if (size64 > int.MaxValue) return false;
                size = (long)size64;
            }
        }
        else if (size32 == 0)
        {
            size = data.Length - offset;
        }
        else
        {
            size = size32;
        }

        if (size < header || size > data.Length - offset) return false;
        box = new Box(type, offset, checked((int)size), header);
        return true;
    }

    private static string FourCc(byte[] data, int offset)
    {
        return new string(new[]
        {
            (char)data[offset],
            (char)data[offset + 1],
            (char)data[offset + 2],
            (char)data[offset + 3]
        });
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static ulong ReadU64(byte[] data, int offset)
    {
        ulong result = 0;
        for (var index = 0; index < 8; index++) result = (result << 8) | data[offset + index];
        return result;
    }

    private static string? FindSingleCapture(
        RecordingPaths paths,
        string? knownCapturePath,
        IList<string> warnings)
    {
        if (knownCapturePath != null) return knownCapturePath;
        var captures = paths.FindCaptureCandidates();
        if (captures.Count == 1) return captures[0];
        if (captures.Count == 0) warnings.Add("No journal-managed capture file was found.");
        else warnings.Add("More than one journal-managed capture file was found.");
        return null;
    }

    private static void Deliver(RecordingPaths paths, string stagingPath)
    {
        if (File.Exists(paths.TargetPath))
        {
            throw new IOException("The target already exists and will not be replaced.");
        }

        if (!string.Equals(
                Path.GetDirectoryName(stagingPath),
                paths.DirectoryPath,
                StringComparison.Ordinal))
        {
            throw new IOException("Recovery staging must be in the target directory.");
        }

        File.Move(stagingPath, paths.TargetPath);
    }

    private static void ValidateStaging(string stagingPath)
    {
        using (var input = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new Mp4Reader(input, leaveOpen: true))
        {
            if (!HasMedia(reader)) throw new Mp4FormatException("The staged MP4 contains no readable media.");
        }
    }

    private static async Task ValidateStagingAsync(string stagingPath, CancellationToken cancellationToken)
    {
        using (var input = OpenRead(stagingPath))
        using (var reader = await Mp4Reader.CreateAsync(input, leaveOpen: true, cancellationToken).ConfigureAwait(false))
        {
            if (!HasMedia(reader)) throw new Mp4FormatException("The staged MP4 contains no readable media.");
        }
    }

    private static bool HasMedia(Mp4Reader reader)
    {
        var hasVideoOrAudio = false;
        foreach (var ignored in reader.ReadVideoNalUnits())
        {
            _ = ignored;
            hasVideoOrAudio = true;
        }

        foreach (var ignored in reader.ReadAudioSamples())
        {
            _ = ignored;
            hasVideoOrAudio = true;
        }

        return hasVideoOrAudio;
    }

    private static bool IsCompletedTargetValid(string targetPath, byte[] expectedHash)
    {
        if (!File.Exists(targetPath)) return false;
        try
        {
            ValidateStaging(targetPath);
            return FixedTimeEquals(HashFile(targetPath), expectedHash);
        }
        catch (Mp4FormatException)
        {
            return false;
        }
    }

    private static async Task<bool> IsCompletedTargetValidAsync(
        string targetPath,
        byte[] expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath)) return false;
        try
        {
            await ValidateStagingAsync(targetPath, cancellationToken).ConfigureAwait(false);
            return FixedTimeEquals(await HashFileAsync(targetPath, cancellationToken).ConfigureAwait(false), expectedHash);
        }
        catch (Mp4FormatException)
        {
            return false;
        }
    }

    private static byte[] HashFile(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return hash.ComputeHash(stream);
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using (var hash = SHA256.Create())
        using (var stream = OpenRead(path))
        {
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.TransformBlock(buffer, 0, read, null, 0);
            }

            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hash.Hash ?? throw new CryptographicException("Could not calculate the completed target hash.");
        }
    }

    private static byte[] ReadAllBytes(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > Mp4Reader.MaximumInputBytes)
            {
                throw new Mp4FormatException("The recovery capture exceeds the managed reader limit.");
            }

            var data = new byte[checked((int)stream.Length)];
            ReadExactly(stream, data, 0, data.Length);
            return data;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        using (var stream = OpenRead(path))
        {
            if (stream.Length > Mp4Reader.MaximumInputBytes)
            {
                throw new Mp4FormatException("The recovery capture exceeds the managed reader limit.");
            }

            var data = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < data.Length)
            {
                var read = await stream
                    .ReadAsync(data, offset, data.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) throw new Mp4FormatException("The recovery capture ended unexpectedly.");
                offset += read;
            }

            return data;
        }
    }

    private static void WritePrefix(string path, byte[] data, int length)
    {
        using (var stream = OpenStaging(path))
        {
            stream.Write(data, 0, length);
            stream.Flush(true);
        }
    }

    private static async Task WritePrefixAsync(
        string path,
        byte[] data,
        int length,
        CancellationToken cancellationToken)
    {
        using (var stream = OpenStaging(path))
        {
            await stream.WriteAsync(data, 0, length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static FileStream OpenJournal(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
    }

    private static FileStream OpenStaging(string path)
    {
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
    }

    private static Mp4WriterOptions CreateWriterOptions(Mp4WriteMode mode)
    {
        return new Mp4WriterOptions { Mode = mode };
    }

    private static void ReadExactly(Stream stream, byte[] data, int offset, int count)
    {
        while (count > 0)
        {
            var read = stream.Read(data, offset, count);
            if (read == 0) throw new Mp4FormatException("The capture ended before a journal-confirmed sample was read.");
            offset += read;
            count -= read;
        }
    }

    private static void FlushJournal(Stream journal)
    {
        var file = journal as FileStream;
        if (file != null)
        {
            file.Flush(true);
            return;
        }

        journal.Flush();
    }

    private static void TryDeleteStagingArtifacts(RecordingPaths paths, IList<string> warnings)
    {
        try
        {
            foreach (var stagingPath in paths.FindStagingCandidates())
            {
                TryDelete(stagingPath, warnings);
            }
        }
        catch (Exception error) when (
            error is IOException ||
            error is UnauthorizedAccessException ||
            error is NotSupportedException)
        {
            warnings.Add("Could not enumerate stale recovery staging artifacts.");
        }
    }

    private static void TryDelete(string path, IList<string> warnings)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (
            error is IOException ||
            error is UnauthorizedAccessException ||
            error is NotSupportedException)
        {
            warnings.Add("Could not remove stale recovery artifact '" + Path.GetFileName(path) + "'.");
        }
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        var different = 0;
        for (var index = 0; index < left.Length; index++) different |= left[index] ^ right[index];
        return different == 0;
    }

    private static Mp4RecordingRecoveryResult NoMedia(IList<string> warnings)
    {
        return new Mp4RecordingRecoveryResult(
            Mp4RecordingRecoveryTier.NoRecoverableMedia,
            null,
            CopyWarnings(warnings));
    }

    private static IReadOnlyList<string> CopyWarnings(IList<string> warnings)
    {
        var copy = new string[warnings.Count];
        for (var index = 0; index < warnings.Count; index++) copy[index] = warnings[index];
        return copy;
    }

    private static void AddWarnings(IList<string> target, IList<string> source)
    {
        foreach (var warning in source) target.Add(warning);
    }

    private enum ExactStatus
    {
        None,
        DeliveredWithoutMarker,
        DeliveredWithMarker
    }

    private sealed class HeuristicVideo
    {
        public HeuristicVideo(VideoCodec codec, VideoCodecConfiguration configuration, IList<byte[]> mediaNals)
        {
            Codec = codec;
            Configuration = configuration;
            MediaNals = mediaNals;
        }

        public VideoCodec Codec { get; }

        public VideoCodecConfiguration Configuration { get; }

        public IList<byte[]> MediaNals { get; }
    }

    private readonly struct Box
    {
        public Box(string type, int start, int size, int headerSize)
        {
            Type = type;
            Start = start;
            Size = size;
            HeaderSize = headerSize;
        }

        public string Type { get; }

        public int Start { get; }

        public int Size { get; }

        public int HeaderSize { get; }

        public int PayloadStart => checked(Start + HeaderSize);

        public int End => checked(Start + Size);
    }
}

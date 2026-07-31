using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DotCore.Mp4;

internal sealed class RecordingPaths
{
    private RecordingPaths(string targetPath, string directoryPath, string targetFileName)
    {
        TargetPath = targetPath;
        DirectoryPath = directoryPath;
        TargetFileName = targetFileName;
        JournalPath = Path.Combine(directoryPath, targetFileName + ".dotcore-journal");
    }

    public string TargetPath { get; }

    public string DirectoryPath { get; }

    public string TargetFileName { get; }

    public string JournalPath { get; }

    public static RecordingPaths Create(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("A recording target path is required.", nameof(targetPath));
        }

        Uri uri;
        if (Uri.TryCreate(targetPath, UriKind.Absolute, out uri) && !uri.IsFile)
        {
            throw new ArgumentException("Recording recovery only supports local file paths.", nameof(targetPath));
        }

        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("The recording target path must name a file.", nameof(targetPath));
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The recording target directory does not exist.");
        }

        return new RecordingPaths(fullPath, directory, fileName);
    }

    public string CapturePath(Guid captureId)
    {
        return Path.Combine(
            DirectoryPath,
            TargetFileName + ".dotcore-capture-" + captureId.ToString("N"));
    }

    public string CreateStagingPath()
    {
        return Path.Combine(
            DirectoryPath,
            TargetFileName + ".dotcore-recover-" + Guid.NewGuid().ToString("N") + ".mp4");
    }

    public byte[] TargetIdentity()
    {
        using (var hash = SHA256.Create())
        {
            return hash.ComputeHash(Encoding.UTF8.GetBytes(TargetPath));
        }
    }

    public IList<string> FindCaptureCandidates()
    {
        return FindManagedCandidates(".dotcore-capture-*");
    }

    public IList<string> FindStagingCandidates()
    {
        return FindManagedCandidates(".dotcore-recover-*.mp4");
    }

    private IList<string> FindManagedCandidates(string suffixPattern)
    {
        var pattern = TargetFileName + suffixPattern;
        var candidates = new List<string>();
        foreach (var candidate in Directory.GetFiles(DirectoryPath, pattern))
        {
            var fullCandidate = Path.GetFullPath(candidate);
            if (string.Equals(
                Path.GetDirectoryName(fullCandidate),
                DirectoryPath,
                StringComparison.Ordinal))
            {
                candidates.Add(fullCandidate);
            }
        }

        return candidates;
    }
}

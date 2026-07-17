using System.Security;
using System.Security.Cryptography;
using Tooltail.Application.Abstractions;

namespace Tooltail.Features.FileSkills.Execution;

/// <summary>
/// A bounded path-based engine for portable tests and the deterministic fixture CLI only.
/// It refuses roots outside one caller-supplied owned workspace. Desktop composition must use
/// the Windows handle-bound implementation instead.
/// </summary>
public sealed class PortableFixtureFileMutationEngine : IFileMutationEngine
{
    private const FileAttributes RetainedAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.Temporary |
        FileAttributes.Offline |
        FileAttributes.Encrypted |
        FileAttributes.Compressed |
        FileAttributes.SparseFile |
        FileAttributes.ReparsePoint;

    private readonly string[] ownedRoots;
    private readonly IFileSystemPathProbe pathProbe;

    public PortableFixtureFileMutationEngine(
        string ownedRoot,
        IFileSystemPathProbe pathProbe)
        : this([ownedRoot], pathProbe)
    {
    }

    public PortableFixtureFileMutationEngine(
        IEnumerable<string> ownedRoots,
        IFileSystemPathProbe pathProbe)
    {
        ArgumentNullException.ThrowIfNull(ownedRoots);
        ArgumentNullException.ThrowIfNull(pathProbe);
        this.ownedRoots = ownedRoots
            .Select(NormalizeRoot)
            .Distinct(PathStringComparer)
            .ToArray();
        if (this.ownedRoots.Length == 0)
        {
            throw new ArgumentException(
                "At least one owned fixture root is required.",
                nameof(ownedRoots));
        }

        this.pathProbe = pathProbe;
    }

    public FileMutationPreparationResult Prepare(FileMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RootIsOwned(request.Root.CanonicalPath))
        {
            return FileMutationPreparationResult.Failure(
                FileMutationFailureKind.InvalidRequest);
        }

        return FileMutationPreparationResult.Success(
            new PreparedFixtureMutation(this, request));
    }

    private FileMutationResult ExecutePrepared(FileMutationRequest request)
    {
        try
        {
            return request.Kind switch
            {
                FileMutationKind.CreateDirectory => CreateDirectory(request),
                FileMutationKind.MoveFile => MoveFile(request),
                FileMutationKind.CopyFile => CopyFile(request),
                FileMutationKind.RemoveCreatedFile => RemoveFile(request),
                FileMutationKind.RemoveCreatedDirectory => RemoveDirectory(request),
                _ => FileMutationResult.Failure(FileMutationFailureKind.InvalidRequest),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.AccessDenied);
        }
        catch (SecurityException)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.AccessDenied);
        }
        catch (DirectoryNotFoundException)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.SourceMissing);
        }
        catch (FileNotFoundException)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.SourceMissing);
        }
        catch (IOException)
        {
            return FileMutationResult.Failure(
                FileMutationFailureKind.IoFailure,
                mutationMayHaveOccurred: request.Kind == FileMutationKind.CopyFile);
        }
    }

    private FileMutationResult CreateDirectory(FileMutationRequest request)
    {
        string destination = Resolve(request, request.DestinationRelativePath!);
        FileSystemPathProbeResult before = pathProbe.Probe(destination);
        if (before.Status == FileSystemPathProbeStatus.Success)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.DestinationExists);
        }

        if (before.Status != FileSystemPathProbeStatus.NotFound)
        {
            return FailureFromProbe(before, source: false);
        }

        Directory.CreateDirectory(destination);
        return DestinationEvidence(destination, destinationCreatedByThisCall: true);
    }

    private FileMutationResult MoveFile(FileMutationRequest request)
    {
        FileMutationResult? sourceFailure = ValidateSource(request, out string source);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        string destination = Resolve(request, request.DestinationRelativePath!);
        if (pathProbe.Probe(destination).Status == FileSystemPathProbeStatus.Success)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.DestinationExists);
        }

        FileInfo sourceInfo = new(source);
        DateTime sourceCreationUtc = sourceInfo.CreationTimeUtc;
        DateTime sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
        FileAttributes attributes = File.GetAttributes(source);
        File.Move(source, destination, overwrite: false);
        File.SetAttributes(destination, FileAttributes.Normal);
        File.SetCreationTimeUtc(destination, sourceCreationUtc);
        File.SetLastWriteTimeUtc(destination, sourceLastWriteUtc);
        File.SetAttributes(destination, attributes);
        return DestinationEvidence(destination, destinationCreatedByThisCall: false);
    }

    private FileMutationResult CopyFile(FileMutationRequest request)
    {
        FileMutationResult? sourceFailure = ValidateSource(request, out string source);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        FileInfo sourceInfo = new(source);
        if (sourceInfo.Length > request.MaximumCopyBytes)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.LimitExceeded);
        }

        string destination = Resolve(request, request.DestinationRelativePath!);
        if (pathProbe.Probe(destination).Status == FileSystemPathProbeStatus.Success)
        {
            return FileMutationResult.Failure(FileMutationFailureKind.DestinationExists);
        }

        DateTime sourceCreationUtc = sourceInfo.CreationTimeUtc;
        DateTime sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
        FileAttributes attributes = File.GetAttributes(source);
        File.Copy(source, destination, overwrite: false);
        File.SetAttributes(destination, FileAttributes.Normal);
        File.SetCreationTimeUtc(destination, sourceCreationUtc);
        File.SetLastWriteTimeUtc(destination, sourceLastWriteUtc);
        File.SetAttributes(destination, attributes);
        return DestinationEvidence(destination, destinationCreatedByThisCall: true);
    }

    private FileMutationResult RemoveFile(FileMutationRequest request)
    {
        FileMutationResult? sourceFailure = ValidateSource(request, out string source);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        File.Delete(source);
        return FileMutationResult.Success();
    }

    private FileMutationResult RemoveDirectory(FileMutationRequest request)
    {
        FileMutationResult? sourceFailure = ValidateSource(request, out string source);
        if (sourceFailure is not null)
        {
            return sourceFailure;
        }

        try
        {
            Directory.Delete(source, recursive: false);
            return FileMutationResult.Success();
        }
        catch (IOException) when (Directory.Exists(source))
        {
            return FileMutationResult.Failure(FileMutationFailureKind.DirectoryNotEmpty);
        }
    }

    private FileMutationResult? ValidateSource(
        FileMutationRequest request,
        out string source)
    {
        source = Resolve(request, request.SourceRelativePath!);
        FileMutationExpectedEntry expected = request.ExpectedSource!;
        FileSystemPathProbeResult probe = pathProbe.Probe(source);
        if (probe.Status != FileSystemPathProbeStatus.Success)
        {
            return FailureFromProbe(probe, source: true);
        }

        if (probe.EntryKind != expected.Kind ||
            probe.IsReparsePoint ||
            !string.Equals(probe.VolumeIdentity, expected.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(probe.EntryIdentity, expected.EntryIdentity, StringComparison.Ordinal))
        {
            return FileMutationResult.Failure(FileMutationFailureKind.SourceChanged);
        }

        FileSystemInfo info = expected.Kind == FileSystemEntryKind.File
            ? new FileInfo(source)
            : new DirectoryInfo(source);
        info.Refresh();
        if (!info.Exists ||
            (expected.CreationUtc is not null &&
             AsUtc(info.CreationTimeUtc) != expected.CreationUtc) ||
            (expected.LastWriteUtc is not null &&
             AsUtc(info.LastWriteTimeUtc) != expected.LastWriteUtc) ||
            (expected.Attributes is not null &&
             (int)(info.Attributes & RetainedAttributes) != expected.Attributes))
        {
            return FileMutationResult.Failure(FileMutationFailureKind.SourceChanged);
        }

        if (expected.Kind == FileSystemEntryKind.File)
        {
            FileInfo file = (FileInfo)info;
            if ((expected.Length is not null && file.Length != expected.Length) ||
                (expected.ContentHash is not null &&
                 !string.Equals(HashFile(source), expected.ContentHash, StringComparison.Ordinal)))
            {
                return FileMutationResult.Failure(FileMutationFailureKind.SourceChanged);
            }
        }

        return null;
    }

    private FileMutationResult DestinationEvidence(
        string destination,
        bool destinationCreatedByThisCall)
    {
        FileSystemPathProbeResult probe = pathProbe.Probe(destination);
        return probe.Status == FileSystemPathProbeStatus.Success &&
            !probe.IsReparsePoint &&
            probe.VolumeIdentity is not null &&
            probe.EntryIdentity is not null
            ? FileMutationResult.Success(
                new FileMutationEvidence(
                    probe.VolumeIdentity,
                    probe.EntryIdentity,
                    destinationCreatedByThisCall))
            : FileMutationResult.Failure(
                FileMutationFailureKind.IoFailure,
                mutationMayHaveOccurred: true);
    }

    private static string Resolve(FileMutationRequest request, string relativePath)
    {
        string platformRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(
            Path.Combine(request.Root.CanonicalPath, platformRelative));
        string normalizedRequestRoot = NormalizeRoot(request.Root.CanonicalPath);
        if (!string.Equals(fullPath, normalizedRequestRoot, PathComparison) &&
            !fullPath.StartsWith(
                normalizedRequestRoot + Path.DirectorySeparatorChar,
                PathComparison))
        {
            throw new ArgumentException("The fixture mutation path escaped its root.");
        }

        return fullPath;
    }

    private bool RootIsOwned(string candidate)
    {
        string normalized = NormalizeRoot(candidate);
        return ownedRoots.Any(ownedRoot =>
            string.Equals(normalized, ownedRoot, PathComparison) ||
            normalized.StartsWith(ownedRoot + Path.DirectorySeparatorChar, PathComparison));
    }

    private static FileMutationResult FailureFromProbe(
        FileSystemPathProbeResult probe,
        bool source) =>
        FileMutationResult.Failure(
            probe.Status switch
            {
                FileSystemPathProbeStatus.NotFound when source =>
                    FileMutationFailureKind.SourceMissing,
                FileSystemPathProbeStatus.AccessDenied =>
                    FileMutationFailureKind.AccessDenied,
                FileSystemPathProbeStatus.Success =>
                    FileMutationFailureKind.DestinationExists,
                _ => FileMutationFailureKind.PathChanged,
            });

    private static string HashFile(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class PreparedFixtureMutation(
        PortableFixtureFileMutationEngine engine,
        FileMutationRequest request) : IPreparedFileMutation
    {
        private bool executed;

        public FileMutationResult Execute()
        {
            ObjectDisposedException.ThrowIf(executed, this);
            executed = true;
            return engine.ExecutePrepared(request);
        }

        public void Dispose() => executed = true;
    }
}

namespace Tooltail.Application.Abstractions;

/// <summary>
/// Prepares one closed file-system mutation, then performs it only when the caller invokes the
/// prepared effect after the final authority check. Production implementations must bind every
/// lookup and effect to retained directory/file handles; an absolute path recheck followed by a
/// path-based mutation does not satisfy this contract.
/// </summary>
public interface IFileMutationEngine
{
    FileMutationPreparationResult Prepare(FileMutationRequest request);
}

public interface IPreparedFileMutation : IDisposable
{
    FileMutationResult Execute();
}

public enum FileMutationKind
{
    CreateDirectory,
    MoveFile,
    CopyFile,
    RemoveCreatedFile,
    RemoveCreatedDirectory,
}

public enum FileMutationFailureKind
{
    None,
    UnsupportedPlatform,
    InvalidRequest,
    RootChanged,
    PathChanged,
    SourceMissing,
    SourceChanged,
    DestinationExists,
    AccessDenied,
    DirectoryNotEmpty,
    LimitExceeded,
    IoFailure,
    CleanupFailed,
}

public sealed record FileMutationRootBinding
{
    public FileMutationRootBinding(
        string canonicalPath,
        string volumeIdentity,
        string entryIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryIdentity);
        CanonicalPath = canonicalPath;
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
    }

    public string CanonicalPath { get; }

    public string VolumeIdentity { get; }

    public string EntryIdentity { get; }
}

public sealed record FileMutationExpectedEntry
{
    public FileMutationExpectedEntry(
        FileSystemEntryKind kind,
        string volumeIdentity,
        string entryIdentity,
        long? length = null,
        DateTimeOffset? creationUtc = null,
        DateTimeOffset? lastWriteUtc = null,
        int? attributes = null,
        string? contentHash = null)
    {
        if (kind is not (FileSystemEntryKind.File or FileSystemEntryKind.Directory))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(volumeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryIdentity);
        if (length is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        RequireUtc(creationUtc, nameof(creationUtc));
        RequireUtc(lastWriteUtc, nameof(lastWriteUtc));
        if (contentHash is not null &&
            (contentHash.Length != 64 || !contentHash.All(IsLowerHex)))
        {
            throw new ArgumentException(
                "A mutation content hash must be 64 lowercase hexadecimal characters.",
                nameof(contentHash));
        }

        if (kind == FileSystemEntryKind.Directory &&
            (length is not null || contentHash is not null))
        {
            throw new ArgumentException(
                "Directory mutation evidence cannot contain file length or content hash.");
        }

        Kind = kind;
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
        Length = length;
        CreationUtc = creationUtc;
        LastWriteUtc = lastWriteUtc;
        Attributes = attributes;
        ContentHash = contentHash;
    }

    public FileSystemEntryKind Kind { get; }

    public string VolumeIdentity { get; }

    public string EntryIdentity { get; }

    public long? Length { get; }

    public DateTimeOffset? CreationUtc { get; }

    public DateTimeOffset? LastWriteUtc { get; }

    public int? Attributes { get; }

    public string? ContentHash { get; }

    private static void RequireUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Mutation evidence timestamps must be UTC.", parameterName);
        }
    }

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';
}

public sealed record FileMutationRequest
{
    private FileMutationRequest(
        FileMutationKind kind,
        FileMutationRootBinding root,
        string? sourceRelativePath,
        string? destinationRelativePath,
        FileMutationExpectedEntry? expectedSource,
        long maximumCopyBytes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCopyBytes);
        Kind = kind;
        Root = root;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        ExpectedSource = expectedSource;
        MaximumCopyBytes = maximumCopyBytes;
        ValidateShape();
    }

    public FileMutationKind Kind { get; }

    public FileMutationRootBinding Root { get; }

    public string? SourceRelativePath { get; }

    public string? DestinationRelativePath { get; }

    public FileMutationExpectedEntry? ExpectedSource { get; }

    public long MaximumCopyBytes { get; }

    public static FileMutationRequest CreateDirectory(
        FileMutationRootBinding root,
        string destinationRelativePath) =>
        new(
            FileMutationKind.CreateDirectory,
            root,
            sourceRelativePath: null,
            destinationRelativePath,
            expectedSource: null,
            maximumCopyBytes: 0);

    public static FileMutationRequest MoveFile(
        FileMutationRootBinding root,
        string sourceRelativePath,
        string destinationRelativePath,
        FileMutationExpectedEntry expectedSource) =>
        new(
            FileMutationKind.MoveFile,
            root,
            sourceRelativePath,
            destinationRelativePath,
            expectedSource,
            maximumCopyBytes: 0);

    public static FileMutationRequest CopyFile(
        FileMutationRootBinding root,
        string sourceRelativePath,
        string destinationRelativePath,
        FileMutationExpectedEntry expectedSource,
        long maximumCopyBytes) =>
        new(
            FileMutationKind.CopyFile,
            root,
            sourceRelativePath,
            destinationRelativePath,
            expectedSource,
            maximumCopyBytes);

    public static FileMutationRequest RemoveCreatedEntry(
        FileMutationRootBinding root,
        string sourceRelativePath,
        FileMutationExpectedEntry expectedSource) =>
        new(
            expectedSource.Kind == FileSystemEntryKind.File
                ? FileMutationKind.RemoveCreatedFile
                : FileMutationKind.RemoveCreatedDirectory,
            root,
            sourceRelativePath,
            destinationRelativePath: null,
            expectedSource,
            maximumCopyBytes: 0);

    private void ValidateShape()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        switch (Kind)
        {
            case FileMutationKind.CreateDirectory:
                RequireAbsent(SourceRelativePath, nameof(SourceRelativePath));
                RequirePresent(DestinationRelativePath, nameof(DestinationRelativePath));
                if (ExpectedSource is not null || MaximumCopyBytes != 0)
                {
                    throw new ArgumentException("A directory create accepts only one destination.");
                }

                break;
            case FileMutationKind.MoveFile:
                RequireFileRelocation(maximumCopyBytesMustBePositive: false);
                break;
            case FileMutationKind.CopyFile:
                RequireFileRelocation(maximumCopyBytesMustBePositive: true);
                break;
            case FileMutationKind.RemoveCreatedFile:
            case FileMutationKind.RemoveCreatedDirectory:
                RequirePresent(SourceRelativePath, nameof(SourceRelativePath));
                RequireAbsent(DestinationRelativePath, nameof(DestinationRelativePath));
                if (ExpectedSource is null || MaximumCopyBytes != 0)
                {
                    throw new ArgumentException("A recovery removal requires exact source evidence.");
                }

                FileSystemEntryKind expectedKind = Kind == FileMutationKind.RemoveCreatedFile
                    ? FileSystemEntryKind.File
                    : FileSystemEntryKind.Directory;
                if (ExpectedSource.Kind != expectedKind)
                {
                    throw new ArgumentException("The recovery removal kind does not match its evidence.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
    }

    private void RequireFileRelocation(bool maximumCopyBytesMustBePositive)
    {
        RequirePresent(SourceRelativePath, nameof(SourceRelativePath));
        RequirePresent(DestinationRelativePath, nameof(DestinationRelativePath));
        if (ExpectedSource?.Kind != FileSystemEntryKind.File ||
            maximumCopyBytesMustBePositive != (MaximumCopyBytes > 0))
        {
            throw new ArgumentException("A file relocation requires file evidence and valid bounds.");
        }
    }

    private static void RequirePresent(string? value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

    private static void RequireAbsent(string? value, string parameterName)
    {
        if (value is not null)
        {
            throw new ArgumentException("This mutation field must be absent.", parameterName);
        }
    }
}

public sealed record FileMutationEvidence
{
    public FileMutationEvidence(
        string volumeIdentity,
        string entryIdentity,
        bool destinationCreatedByThisCall)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryIdentity);
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
        DestinationCreatedByThisCall = destinationCreatedByThisCall;
    }

    public string VolumeIdentity { get; }

    public string EntryIdentity { get; }

    public bool DestinationCreatedByThisCall { get; }
}

public sealed record FileMutationPreparationResult
{
    private FileMutationPreparationResult(
        bool isSuccess,
        FileMutationFailureKind failureKind,
        IPreparedFileMutation? preparedMutation)
    {
        if (isSuccess != (failureKind == FileMutationFailureKind.None) ||
            isSuccess != (preparedMutation is not null))
        {
            throw new ArgumentException("The mutation preparation result shape is inconsistent.");
        }

        IsSuccess = isSuccess;
        FailureKind = failureKind;
        PreparedMutation = preparedMutation;
    }

    public bool IsSuccess { get; }

    public FileMutationFailureKind FailureKind { get; }

    public IPreparedFileMutation? PreparedMutation { get; }

    public static FileMutationPreparationResult Success(
        IPreparedFileMutation preparedMutation)
    {
        ArgumentNullException.ThrowIfNull(preparedMutation);
        return new FileMutationPreparationResult(
            isSuccess: true,
            FileMutationFailureKind.None,
            preparedMutation);
    }

    public static FileMutationPreparationResult Failure(
        FileMutationFailureKind failureKind)
    {
        if (failureKind == FileMutationFailureKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        return new FileMutationPreparationResult(
            isSuccess: false,
            failureKind,
            preparedMutation: null);
    }
}

public sealed record FileMutationResult
{
    private FileMutationResult(
        bool isSuccess,
        FileMutationFailureKind failureKind,
        bool mutationMayHaveOccurred,
        FileMutationEvidence? evidence)
    {
        if (isSuccess != (failureKind == FileMutationFailureKind.None) ||
            (isSuccess && mutationMayHaveOccurred) ||
            (!isSuccess && evidence is not null))
        {
            throw new ArgumentException("The mutation result shape is inconsistent.");
        }

        IsSuccess = isSuccess;
        FailureKind = failureKind;
        MutationMayHaveOccurred = mutationMayHaveOccurred;
        Evidence = evidence;
    }

    public bool IsSuccess { get; }

    public FileMutationFailureKind FailureKind { get; }

    public bool MutationMayHaveOccurred { get; }

    public FileMutationEvidence? Evidence { get; }

    public static FileMutationResult Success(FileMutationEvidence? evidence = null) =>
        new(
            true,
            FileMutationFailureKind.None,
            mutationMayHaveOccurred: false,
            evidence);

    public static FileMutationResult Failure(
        FileMutationFailureKind failureKind,
        bool mutationMayHaveOccurred = false)
    {
        if (failureKind == FileMutationFailureKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        return new FileMutationResult(
            isSuccess: false,
            failureKind,
            mutationMayHaveOccurred,
            evidence: null);
    }
}

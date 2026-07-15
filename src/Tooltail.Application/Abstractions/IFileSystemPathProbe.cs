namespace Tooltail.Application.Abstractions;

/// <summary>
/// Reads identity-bearing path metadata without granting mutation authority.
/// Implementations must inspect the named entry itself rather than following a reparse point.
/// </summary>
public interface IFileSystemPathProbe
{
    FileSystemPathProbeResult Probe(string absolutePath);
}

public enum FileSystemPathProbeStatus
{
    Success,
    NotFound,
    AccessDenied,
    InvalidPath,
    Unsupported,
    IoFailure,
}

public enum FileSystemEntryKind
{
    File,
    Directory,
    Other,
}

public sealed record FileSystemPathProbeResult
{
    private FileSystemPathProbeResult(
        FileSystemPathProbeStatus status,
        string reasonCode,
        string? canonicalPath,
        FileSystemEntryKind? entryKind,
        string? volumeIdentity,
        string? entryIdentity,
        bool isReparsePoint,
        bool isLocalFixedDrive)
    {
        Status = status;
        ReasonCode = reasonCode;
        CanonicalPath = canonicalPath;
        EntryKind = entryKind;
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
        IsReparsePoint = isReparsePoint;
        IsLocalFixedDrive = isLocalFixedDrive;
    }

    public FileSystemPathProbeStatus Status { get; }

    public string ReasonCode { get; }

    public string? CanonicalPath { get; }

    public FileSystemEntryKind? EntryKind { get; }

    public string? VolumeIdentity { get; }

    public string? EntryIdentity { get; }

    public bool IsReparsePoint { get; }

    public bool IsLocalFixedDrive { get; }

    public static FileSystemPathProbeResult Found(
        string canonicalPath,
        FileSystemEntryKind entryKind,
        string volumeIdentity,
        string entryIdentity,
        bool isReparsePoint,
        bool isLocalFixedDrive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryIdentity);

        return new FileSystemPathProbeResult(
            FileSystemPathProbeStatus.Success,
            "filesystem.probe_succeeded",
            canonicalPath,
            entryKind,
            volumeIdentity,
            entryIdentity,
            isReparsePoint,
            isLocalFixedDrive);
    }

    public static FileSystemPathProbeResult Failed(
        FileSystemPathProbeStatus status,
        string reasonCode)
    {
        if (status == FileSystemPathProbeStatus.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new FileSystemPathProbeResult(
            status,
            reasonCode,
            null,
            null,
            null,
            null,
            isReparsePoint: false,
            isLocalFixedDrive: false);
    }
}

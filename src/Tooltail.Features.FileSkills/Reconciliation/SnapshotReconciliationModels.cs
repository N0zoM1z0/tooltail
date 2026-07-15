using System.Collections.ObjectModel;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Reconciliation;

public enum ReconciledEffectKind
{
    Created,
    Renamed,
    Moved,
    Copied,
    Modified,
    Deleted,
    Ambiguous,
    Concurrent,
    Unsupported,
}

public enum SnapshotReconciliationStatus
{
    Complete,
    IncompleteSnapshot,
    WatcherOverflow,
    WatcherFault,
    WatcherNotQuiesced,
    RootMismatch,
    Concurrent,
    Ambiguous,
    Unsupported,
}

public sealed record ReconciledFileEffect
{
    public ReconciledFileEffect(
        ReconciledEffectKind kind,
        string? sourceRelativePath,
        string? destinationRelativePath,
        FolderSnapshotEntry? before,
        FolderSnapshotEntry? after,
        string reasonCode,
        IEnumerable<string>? candidateSourcePaths = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        string[] candidates = (candidateSourcePaths ?? []).ToArray();
        if (candidates.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Candidate source paths cannot be blank.", nameof(candidateSourcePaths));
        }

        ValidateShape(kind, sourceRelativePath, destinationRelativePath, before, after);
        Kind = kind;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        Before = before;
        After = after;
        ReasonCode = reasonCode;
        CandidateSourcePaths = new ReadOnlyCollection<string>(
            candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .ToArray());
    }

    public ReconciledEffectKind Kind { get; }

    public string? SourceRelativePath { get; }

    public string? DestinationRelativePath { get; }

    public FolderSnapshotEntry? Before { get; }

    public FolderSnapshotEntry? After { get; }

    public string ReasonCode { get; }

    public IReadOnlyList<string> CandidateSourcePaths { get; }

    public SnapshotEntryKind EntryKind => Before?.Kind ?? After?.Kind ?? SnapshotEntryKind.Other;

    public bool IsSupportedForCompilation =>
        Kind switch
        {
            ReconciledEffectKind.Created =>
                After is { Kind: SnapshotEntryKind.Directory, IsReparsePoint: false },
            ReconciledEffectKind.Renamed or
            ReconciledEffectKind.Moved or
            ReconciledEffectKind.Copied =>
                Before is { Kind: SnapshotEntryKind.File, IsReparsePoint: false } &&
                After is { Kind: SnapshotEntryKind.File, IsReparsePoint: false },
            _ => false,
        };

    private static void ValidateShape(
        ReconciledEffectKind kind,
        string? sourceRelativePath,
        string? destinationRelativePath,
        FolderSnapshotEntry? before,
        FolderSnapshotEntry? after)
    {
        bool valid = kind switch
        {
            ReconciledEffectKind.Created =>
                sourceRelativePath is null &&
                destinationRelativePath is not null &&
                before is null &&
                after?.RelativePath == destinationRelativePath,
            ReconciledEffectKind.Deleted =>
                sourceRelativePath is not null &&
                destinationRelativePath is null &&
                before?.RelativePath == sourceRelativePath &&
                after is null,
            ReconciledEffectKind.Renamed or
            ReconciledEffectKind.Moved or
            ReconciledEffectKind.Copied =>
                sourceRelativePath is not null &&
                destinationRelativePath is not null &&
                before?.RelativePath == sourceRelativePath &&
                after?.RelativePath == destinationRelativePath,
            ReconciledEffectKind.Modified =>
                sourceRelativePath is not null &&
                destinationRelativePath is not null &&
                string.Equals(sourceRelativePath, destinationRelativePath, StringComparison.Ordinal) &&
                before?.RelativePath == sourceRelativePath &&
                after?.RelativePath == destinationRelativePath,
            _ => true,
        };

        if (!valid)
        {
            throw new ArgumentException("The reconciled effect shape does not match its kind.");
        }
    }
}

public sealed record SnapshotReconciliation
{
    internal SnapshotReconciliation(
        SnapshotReconciliationStatus status,
        string reasonCode,
        IEnumerable<ReconciledFileEffect> effects)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(effects);
        ReconciledFileEffect[] materialized = effects.ToArray();
        if (materialized.Any(static effect => effect is null))
        {
            throw new ArgumentException("Reconciliation effects cannot contain null values.", nameof(effects));
        }

        Status = status;
        ReasonCode = reasonCode;
        Effects = new ReadOnlyCollection<ReconciledFileEffect>(materialized);
    }

    public SnapshotReconciliationStatus Status { get; }

    public string ReasonCode { get; }

    public IReadOnlyList<ReconciledFileEffect> Effects { get; }

    public bool IsCompilable =>
        Status == SnapshotReconciliationStatus.Complete &&
        Effects.Count > 0 &&
        Effects.All(static effect => effect.IsSupportedForCompilation);
}

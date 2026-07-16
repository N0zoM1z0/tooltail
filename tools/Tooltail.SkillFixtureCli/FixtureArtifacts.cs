using System.Text.Json;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.SkillFixtureCli;

internal sealed record FixtureValueResult<T>(bool IsSuccess, string ReasonCode, T? Value)
{
    public static FixtureValueResult<T> Success(T value) =>
        new(true, "fixture.artifact_valid", value);

    public static FixtureValueResult<T> Failure(string reasonCode) =>
        new(false, reasonCode, default);
}

internal sealed record FixtureSnapshotDocument
{
    public required string ContractVersion { get; init; }

    public required string RootIdentity { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset CompletedUtc { get; init; }

    public required FolderSnapshotStatus Status { get; init; }

    public string? ReasonCode { get; init; }

    public required long HashedBytes { get; init; }

    public required IReadOnlyList<FixtureSnapshotEntryDocument> Entries { get; init; }
}

internal sealed record FixtureSnapshotEntryDocument
{
    public required string RelativePath { get; init; }

    public required SnapshotEntryKind Kind { get; init; }

    public long? Length { get; init; }

    public required DateTimeOffset CreationUtc { get; init; }

    public required DateTimeOffset LastWriteUtc { get; init; }

    public required int Attributes { get; init; }

    public required bool IsReparsePoint { get; init; }

    public string? VolumeIdentity { get; init; }

    public string? EntryIdentity { get; init; }

    public required SnapshotContentHashStatus ContentHashStatus { get; init; }

    public string? ContentSha256 { get; init; }
}

internal sealed record FixtureReconciliationDocument
{
    public required string ContractVersion { get; init; }

    public required SnapshotReconciliationStatus Status { get; init; }

    public required string ReasonCode { get; init; }

    public required IReadOnlyList<FixtureEffectDocument> Effects { get; init; }
}

internal sealed record FixtureEffectDocument
{
    public required ReconciledEffectKind Kind { get; init; }

    public string? SourceRelativePath { get; init; }

    public string? DestinationRelativePath { get; init; }

    public required string ReasonCode { get; init; }

    public required IReadOnlyList<string> CandidateSourcePaths { get; init; }
}

internal sealed record FixtureTreeEntry(
    string RelativePath,
    SnapshotEntryKind Kind,
    long? Length,
    string? ContentSha256);

internal sealed record FixtureVerifiedStep(
    int Sequence,
    FilePrimitive Primitive,
    string? SourceRelativePath,
    string DestinationRelativePath,
    bool DestinationWasAbsent,
    VerifiedEntryKind DestinationKind,
    long? Length,
    string? ContentSha256);

internal sealed record FixtureReceiptProjection(
    Guid ReceiptId,
    Guid ExecutionId,
    Guid PlanId,
    string PlanFingerprint,
    DateTimeOffset CompletedUtc,
    DateTimeOffset? UndoAvailableUntilUtc,
    IReadOnlyList<FixtureVerifiedStep> VerifiedSteps);

internal sealed record FixtureRecoveryStep(
    int Sequence,
    int OriginalSequence,
    RecoveryPrimitive Primitive,
    string SourceRelativePath,
    string? DestinationRelativePath,
    VerifiedEntryKind RecoveredKind,
    long? Length,
    string? ContentSha256);

internal sealed record FixtureRecoveryReceiptProjection(
    Guid ReceiptId,
    Guid ExecutionId,
    Guid PlanId,
    string PlanFingerprint,
    Guid OriginalExecutionId,
    Guid OriginalPlanId,
    string OriginalPlanFingerprint,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<FixtureRecoveryStep> VerifiedSteps);

internal static class FixtureArtifacts
{
    public const string SnapshotContractVersion = "tooltail.fixture-snapshot/1";
    public const string ReconciliationContractVersion = "tooltail.fixture-reconciliation/1";

    public static byte[] EncodeSnapshot(FolderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        FixtureSnapshotDocument document = new()
        {
            ContractVersion = SnapshotContractVersion,
            RootIdentity = snapshot.RootIdentity.Value,
            StartedUtc = snapshot.StartedUtc,
            CompletedUtc = snapshot.CompletedUtc,
            Status = snapshot.Status,
            ReasonCode = snapshot.ReasonCode,
            HashedBytes = snapshot.HashedBytes,
            Entries = snapshot.Entries.Select(static entry => new FixtureSnapshotEntryDocument
            {
                RelativePath = entry.RelativePath,
                Kind = entry.Kind,
                Length = entry.Length,
                CreationUtc = entry.CreationUtc,
                LastWriteUtc = entry.LastWriteUtc,
                Attributes = (int)entry.Attributes,
                IsReparsePoint = entry.IsReparsePoint,
                VolumeIdentity = entry.VolumeIdentity,
                EntryIdentity = entry.EntryIdentity,
                ContentHashStatus = entry.ContentHashStatus,
                ContentSha256 = entry.ContentHash?.Value,
            }).ToArray(),
        };
        return FixtureJson.Serialize(document);
    }

    public static FixtureValueResult<FolderSnapshot> DecodeSnapshot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > FixtureWorkspace.MaximumArtifactBytes)
        {
            return FixtureValueResult<FolderSnapshot>.Failure(
                "fixture.snapshot_artifact_size_invalid");
        }

        try
        {
            FixtureSnapshotDocument? document = FixtureJson.Deserialize<FixtureSnapshotDocument>(
                bytes);
            if (document is null ||
                document.ContractVersion != SnapshotContractVersion ||
                document.Entries is null ||
                document.Entries.Count > 10_000)
            {
                return FixtureValueResult<FolderSnapshot>.Failure(
                    "fixture.snapshot_artifact_invalid");
            }

            FolderSnapshotEntry[] entries = document.Entries.Select(static entry =>
                new FolderSnapshotEntry(
                    entry.RelativePath,
                    entry.Kind,
                    entry.Length,
                    entry.CreationUtc,
                    entry.LastWriteUtc,
                    (FileAttributes)entry.Attributes,
                    entry.IsReparsePoint,
                    entry.VolumeIdentity,
                    entry.EntryIdentity,
                    entry.ContentHashStatus,
                    entry.ContentSha256 is null
                        ? null
                        : new ContentHash(entry.ContentSha256))).ToArray();
            DomainResult<FolderSnapshot> restored = FolderSnapshot.Rehydrate(
                new ResourceRootIdentity(document.RootIdentity),
                document.StartedUtc,
                document.CompletedUtc,
                document.Status,
                document.ReasonCode,
                document.HashedBytes,
                entries);
            return restored.IsSuccess
                ? FixtureValueResult<FolderSnapshot>.Success(restored.Value!)
                : FixtureValueResult<FolderSnapshot>.Failure(restored.Error!.Code);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or OverflowException)
        {
            return FixtureValueResult<FolderSnapshot>.Failure(
                "fixture.snapshot_artifact_invalid");
        }
    }

    public static byte[] EncodeReconciliation(SnapshotReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        FixtureReconciliationDocument document = new()
        {
            ContractVersion = ReconciliationContractVersion,
            Status = reconciliation.Status,
            ReasonCode = reconciliation.ReasonCode,
            Effects = reconciliation.Effects.Select(static effect => new FixtureEffectDocument
            {
                Kind = effect.Kind,
                SourceRelativePath = effect.SourceRelativePath,
                DestinationRelativePath = effect.DestinationRelativePath,
                ReasonCode = effect.ReasonCode,
                CandidateSourcePaths = effect.CandidateSourcePaths,
            }).ToArray(),
        };
        return FixtureJson.Serialize(document);
    }

    public static FixtureValueResult<FixtureReconciliationDocument> DecodeReconciliation(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > FixtureWorkspace.MaximumArtifactBytes)
        {
            return FixtureValueResult<FixtureReconciliationDocument>.Failure(
                "fixture.reconciliation_artifact_size_invalid");
        }

        try
        {
            FixtureReconciliationDocument? document =
                FixtureJson.Deserialize<FixtureReconciliationDocument>(bytes);
            if (document is null ||
                document.ContractVersion != ReconciliationContractVersion ||
                !Enum.IsDefined(document.Status) ||
                !IsBoundedReason(document.ReasonCode) ||
                document.Effects is null ||
                document.Effects.Count > 10_000 ||
                document.Effects.Any(static effect => !IsValidEffectDocument(effect)))
            {
                return FixtureValueResult<FixtureReconciliationDocument>.Failure(
                    "fixture.reconciliation_artifact_invalid");
            }

            return FixtureValueResult<FixtureReconciliationDocument>.Success(document);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or OverflowException)
        {
            return FixtureValueResult<FixtureReconciliationDocument>.Failure(
                "fixture.reconciliation_artifact_invalid");
        }
    }

    public static IReadOnlyList<FixtureTreeEntry> Tree(FolderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Entries
            .Select(static entry => new FixtureTreeEntry(
                entry.RelativePath,
                entry.Kind,
                entry.Length,
                entry.ContentHash?.Value))
            .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static FixtureReceiptProjection Project(ExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new FixtureReceiptProjection(
            receipt.Id.Value,
            receipt.ExecutionId.Value,
            receipt.PlanId.Value,
            receipt.PlanFingerprint.Value,
            receipt.CompletedUtc,
            receipt.UndoAvailableUntilUtc,
            receipt.VerifiedSteps.Select(static step => new FixtureVerifiedStep(
                step.StepSequence,
                step.Primitive,
                step.SourceRelativePath,
                step.DestinationRelativePath,
                step.DestinationWasAbsent,
                step.Destination.Kind,
                step.Destination.Length,
                step.Destination.ContentHash?.Value)).ToArray());
    }

    public static FixtureRecoveryReceiptProjection Project(RecoveryExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new FixtureRecoveryReceiptProjection(
            receipt.Id.Value,
            receipt.ExecutionId.Value,
            receipt.PlanId.Value,
            receipt.PlanFingerprint.Value,
            receipt.OriginalExecutionId.Value,
            receipt.OriginalPlanId.Value,
            receipt.OriginalPlanFingerprint.Value,
            receipt.CompletedUtc,
            receipt.VerifiedSteps.Select(static step => new FixtureRecoveryStep(
                step.StepSequence,
                step.OriginalStepSequence,
                step.Primitive,
                step.SourceRelativePath,
                step.DestinationRelativePath,
                step.RecoveredEntry.Kind,
                step.RecoveredEntry.Length,
                step.RecoveredEntry.ContentHash?.Value)).ToArray());
    }

    private static bool IsValidEffectDocument(FixtureEffectDocument? effect) =>
        effect is not null &&
        Enum.IsDefined(effect.Kind) &&
        IsBoundedReason(effect.ReasonCode) &&
        IsOptionalRelativePath(effect.SourceRelativePath) &&
        IsOptionalRelativePath(effect.DestinationRelativePath) &&
        effect.CandidateSourcePaths is not null &&
        effect.CandidateSourcePaths.Count <= 10_000 &&
        effect.CandidateSourcePaths.All(static path => IsOptionalRelativePath(path));

    private static bool IsOptionalRelativePath(string? value) =>
        value is null ||
        (value.Length <= WindowsPathPolicy.MaximumRelativePathLength &&
         WindowsPathPolicy.ParseRelative(value).IsSuccess);

    private static bool IsBoundedReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 160 &&
        !value.Any(char.IsControl);
}

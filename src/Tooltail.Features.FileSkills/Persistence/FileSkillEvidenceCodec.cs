using System.Text.Json;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Common;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Persistence;

public sealed record EvidenceReadResult<T>(bool IsSuccess, string ReasonCode, T? Value)
    where T : class;

public static class EvidenceReadResult
{
    public static EvidenceReadResult<T> Success<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(true, "evidence.read", value);
    }

    public static EvidenceReadResult<T> Failure<T>(string reasonCode)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new(false, reasonCode, null);
    }
}

public static class FileSkillEvidenceCodec
{
    public const string SnapshotContractVersion = "tooltail.folder-snapshot/1";
    public const string ReconciliationContractVersion = "tooltail.reconciliation-summary/1";
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private const int MaximumEntries = 10_000;
    private const int MaximumEffects = 10_000;

    public static byte[] EncodeSnapshot(FolderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] bytes = ContractJson.Serialize(
            new SnapshotDocument
            {
                ContractVersion = SnapshotContractVersion,
                RootIdentity = snapshot.RootIdentity.Value,
                StartedUtc = snapshot.StartedUtc,
                CompletedUtc = snapshot.CompletedUtc,
                Status = snapshot.Status,
                ReasonCode = snapshot.ReasonCode,
                HashedBytes = snapshot.HashedBytes,
                Entries = snapshot.Entries.Select(static entry => new SnapshotEntryDocument
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
            });
        return bytes.Length <= MaximumDocumentBytes
            ? bytes
            : throw new ArgumentException("The snapshot evidence exceeds its persisted byte bound.", nameof(snapshot));
    }

    public static EvidenceReadResult<FolderSnapshot> DecodeSnapshot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes)
        {
            return EvidenceReadResult.Failure<FolderSnapshot>("evidence.snapshot_size_invalid");
        }

        try
        {
            SnapshotDocument? document = JsonSerializer.Deserialize<SnapshotDocument>(
                bytes,
                ContractJson.SerializerOptions);
            if (document is null ||
                !string.Equals(
                    document.ContractVersion,
                    SnapshotContractVersion,
                    StringComparison.Ordinal) ||
                document.Entries is null ||
                document.Entries.Length > MaximumEntries)
            {
                return EvidenceReadResult.Failure<FolderSnapshot>("evidence.snapshot_invalid");
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
                ? EvidenceReadResult.Success(restored.Value!)
                : EvidenceReadResult.Failure<FolderSnapshot>(restored.Error!.Code);
        }
        catch (Exception exception) when (exception is JsonException or
            ArgumentException or OverflowException or NotSupportedException)
        {
            return EvidenceReadResult.Failure<FolderSnapshot>("evidence.snapshot_invalid");
        }
    }

    public static byte[] EncodeReconciliationSummary(
        SnapshotReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        if (reconciliation.Effects.Count > MaximumEffects)
        {
            throw new ArgumentException(
                "The reconciliation exceeds its persisted effect bound.",
                nameof(reconciliation));
        }

        byte[] bytes = ContractJson.Serialize(
            new ReconciliationDocument
            {
                ContractVersion = ReconciliationContractVersion,
                Status = reconciliation.Status,
                ReasonCode = reconciliation.ReasonCode,
                Effects = reconciliation.Effects.Select(static effect => new EffectDocument
                {
                    Kind = effect.Kind,
                    SourceRelativePath = effect.SourceRelativePath,
                    DestinationRelativePath = effect.DestinationRelativePath,
                    ReasonCode = effect.ReasonCode,
                    CandidateSourcePaths = effect.CandidateSourcePaths.ToArray(),
                }).ToArray(),
            });
        return bytes.Length <= MaximumDocumentBytes
            ? bytes
            : throw new ArgumentException(
                "The reconciliation evidence exceeds its persisted byte bound.",
                nameof(reconciliation));
    }

    private sealed record SnapshotDocument
    {
        public required string ContractVersion { get; init; }
        public required string RootIdentity { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }
        public required DateTimeOffset CompletedUtc { get; init; }
        public required FolderSnapshotStatus Status { get; init; }
        public string? ReasonCode { get; init; }
        public required long HashedBytes { get; init; }
        public required SnapshotEntryDocument[] Entries { get; init; }
    }

    private sealed record SnapshotEntryDocument
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

    private sealed record ReconciliationDocument
    {
        public required string ContractVersion { get; init; }
        public required SnapshotReconciliationStatus Status { get; init; }
        public required string ReasonCode { get; init; }
        public required EffectDocument[] Effects { get; init; }
    }

    private sealed record EffectDocument
    {
        public required ReconciledEffectKind Kind { get; init; }
        public string? SourceRelativePath { get; init; }
        public string? DestinationRelativePath { get; init; }
        public required string ReasonCode { get; init; }
        public required string[] CandidateSourcePaths { get; init; }
    }
}

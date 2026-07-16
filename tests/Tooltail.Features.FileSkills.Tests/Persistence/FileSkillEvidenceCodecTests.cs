using System.Security.Cryptography;
using System.Text;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Persistence;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Tests.Persistence;

public sealed class FileSkillEvidenceCodecTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SnapshotRoundTripRetainsExactIdentityAndHashEvidence()
    {
        FolderSnapshot source = Snapshot();

        byte[] bytes = FileSkillEvidenceCodec.EncodeSnapshot(source);
        EvidenceReadResult<FolderSnapshot> restored =
            FileSkillEvidenceCodec.DecodeSnapshot(bytes);

        Assert.True(restored.IsSuccess, restored.ReasonCode);
        Assert.Equal(source.RootIdentity, restored.Value!.RootIdentity);
        Assert.Equal(source.StartedUtc, restored.Value.StartedUtc);
        Assert.Equal(source.CompletedUtc, restored.Value.CompletedUtc);
        Assert.Equal(source.HashedBytes, restored.Value.HashedBytes);
        Assert.Equal(source.Entries, restored.Value.Entries);
    }

    [Fact]
    public void UnknownFieldsAndHashByteTamperingFailClosed()
    {
        string json = Encoding.UTF8.GetString(
            FileSkillEvidenceCodec.EncodeSnapshot(Snapshot()));
        byte[] unknown = Encoding.UTF8.GetBytes(
            json.Insert(1, "\"unexpected\":true,"));
        byte[] changedHashBudget = Encoding.UTF8.GetBytes(
            json.Replace("\"hashedBytes\":7", "\"hashedBytes\":6", StringComparison.Ordinal));

        EvidenceReadResult<FolderSnapshot> unknownResult =
            FileSkillEvidenceCodec.DecodeSnapshot(unknown);
        EvidenceReadResult<FolderSnapshot> budgetResult =
            FileSkillEvidenceCodec.DecodeSnapshot(changedHashBudget);

        Assert.False(unknownResult.IsSuccess);
        Assert.Equal("evidence.snapshot_invalid", unknownResult.ReasonCode);
        Assert.False(budgetResult.IsSuccess);
        Assert.Equal("snapshot.rehydrate_invalid", budgetResult.ReasonCode);
    }

    [Fact]
    public void OversizedSnapshotIsRejectedBeforeParsing()
    {
        byte[] bytes = new byte[FileSkillEvidenceCodec.MaximumDocumentBytes + 1];

        EvidenceReadResult<FolderSnapshot> result =
            FileSkillEvidenceCodec.DecodeSnapshot(bytes);

        Assert.False(result.IsSuccess);
        Assert.Equal("evidence.snapshot_size_invalid", result.ReasonCode);
    }

    private static FolderSnapshot Snapshot()
    {
        const string content = "invoice";
        ContentHash hash = new(Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))));
        FolderSnapshotEntry file = new(
            "invoice-alpha.pdf",
            SnapshotEntryKind.File,
            content.Length,
            Now.AddDays(-1),
            Now,
            FileAttributes.Archive,
            isReparsePoint: false,
            "volume",
            "entry",
            SnapshotContentHashStatus.Computed,
            hash);
        return FolderSnapshot.Rehydrate(
            new ResourceRootIdentity("winfs-v1:volume:root"),
            Now,
            Now.AddSeconds(1),
            FolderSnapshotStatus.Complete,
            reasonCode: null,
            hashedBytes: content.Length,
            [file]).Value!;
    }
}

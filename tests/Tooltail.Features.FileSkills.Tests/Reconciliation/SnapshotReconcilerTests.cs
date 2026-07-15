using System.Security.Cryptography;
using System.Text;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Tests.Reconciliation;

public sealed class SnapshotReconcilerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StableFileIdentityClassifiesRenameWithoutWatcherAuthority()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("Inbox\\report.txt", "content", "file-1"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("Inbox\\report-final.txt", "content", "file-1"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Complete, result.Status);
        Assert.True(result.IsCompilable);
        ReconciledFileEffect effect = Assert.Single(result.Effects);
        Assert.Equal(ReconciledEffectKind.Renamed, effect.Kind);
        Assert.Equal("Inbox\\report.txt", effect.SourceRelativePath);
        Assert.Equal("Inbox\\report-final.txt", effect.DestinationRelativePath);
    }

    [Fact]
    public void ReorderedAndDuplicateRenameHintsProduceIdenticalReconciliation()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("Inbox\\report.txt", "content", "path-identity-before"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("Archive\\report.txt", "content", "path-identity-after"));
        WatcherHint rename = new(
            WatcherHintKind.Renamed,
            "Archive\\report.txt",
            "Inbox\\report.txt");
        WatcherHint noise = new(WatcherHintKind.Changed, "Inbox\\report.txt");

        SnapshotReconciliation first = SnapshotReconciler.Reconcile(
            baseline,
            final,
            new WatcherHintBatch([rename, noise, rename], overflowed: false, quiesced: true));
        SnapshotReconciliation second = SnapshotReconciler.Reconcile(
            baseline,
            final,
            new WatcherHintBatch([noise, rename], overflowed: false, quiesced: true));

        Assert.Equal(SnapshotReconciliationStatus.Complete, first.Status);
        Assert.Equal(ReconciledEffectKind.Moved, Assert.Single(first.Effects).Kind);
        Assert.Equal(Project(first), Project(second));
    }

    [Fact]
    public void CreatedDirectoryMoveAndUniqueCopyRemainCompilable()
    {
        FolderSnapshotEntry moving = File("Inbox\\report.txt", "report", "moving");
        FolderSnapshotEntry template = File("Template.txt", "template", "template");
        FolderSnapshot baseline = Snapshot(isFinal: false, moving, template);
        FolderSnapshot final = Snapshot(
            isFinal: true,
            Directory("Archive", "directory-archive"),
            File("Archive\\report.txt", "report", "moving"),
            template,
            File("Archive\\Template.txt", "template", "copy"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Complete, result.Status);
        Assert.True(result.IsCompilable);
        Assert.Equal(
            [
                ReconciledEffectKind.Created,
                ReconciledEffectKind.Moved,
                ReconciledEffectKind.Copied,
            ],
            result.Effects.Select(static effect => effect.Kind));
    }

    [Fact]
    public void DeletionAndModificationAreExplainedButNeverCompilable()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("delete.txt", "old", "delete"),
            File("modify.txt", "before", "modify"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("modify.txt", "after", "modify", lastWriteOffsetMinutes: 1));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Unsupported, result.Status);
        Assert.False(result.IsCompilable);
        Assert.Contains(result.Effects, static effect => effect.Kind == ReconciledEffectKind.Modified);
        Assert.Contains(result.Effects, static effect => effect.Kind == ReconciledEffectKind.Deleted);
    }

    [Fact]
    public void OverflowAndUnquiescedHintsInvalidateOtherwiseValidEvidence()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("before.txt", "content", "same"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("after.txt", "content", "same"));

        SnapshotReconciliation overflow = SnapshotReconciler.Reconcile(
            baseline,
            final,
            new WatcherHintBatch([], overflowed: true, quiesced: true));
        SnapshotReconciliation unquiesced = SnapshotReconciler.Reconcile(
            baseline,
            final,
            new WatcherHintBatch([], overflowed: false, quiesced: false));

        Assert.Equal(SnapshotReconciliationStatus.WatcherOverflow, overflow.Status);
        Assert.Equal(SnapshotReconciliationStatus.WatcherNotQuiesced, unquiesced.Status);
        Assert.False(overflow.IsCompilable);
        Assert.False(unquiesced.IsCompilable);
    }

    [Fact]
    public void IncompleteSnapshotCannotBeReconciledFromHints()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("before.txt", "content", "same"));
        FolderSnapshot incomplete = new(
            baseline.RootIdentity,
            Now.AddMinutes(2),
            Now.AddMinutes(2),
            FolderSnapshotStatus.Cancelled,
            "snapshot.cancelled",
            hashedBytes: 0,
            entries: []);
        WatcherHint rename = new(
            WatcherHintKind.Renamed,
            "after.txt",
            "before.txt");

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            incomplete,
            new WatcherHintBatch([rename], overflowed: false, quiesced: true));

        Assert.Equal(SnapshotReconciliationStatus.IncompleteSnapshot, result.Status);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public void ReparseEvidenceIsExplicitlyUnsupported()
    {
        FolderSnapshotEntry link = new(
            "Link",
            SnapshotEntryKind.Directory,
            length: null,
            Now,
            Now,
            FileAttributes.ReparsePoint,
            isReparsePoint: true,
            "volume",
            "link",
            SnapshotContentHashStatus.NotApplicable,
            contentHash: null);
        FolderSnapshot baseline = Snapshot(isFinal: false, link);
        FolderSnapshot final = Snapshot(isFinal: true, link);

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Unsupported, result.Status);
        Assert.Equal(ReconciledEffectKind.Unsupported, Assert.Single(result.Effects).Kind);
        Assert.Equal("reconcile.reparse_point_unsupported", result.Effects[0].ReasonCode);
    }

    [Fact]
    public void EntryReplacementAtSamePathIsConcurrentNotAValidModification()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("file.txt", "content", "before-identity"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("file.txt", "content", "after-identity"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Concurrent, result.Status);
        Assert.Equal(ReconciledEffectKind.Concurrent, Assert.Single(result.Effects).Kind);
    }

    [Fact]
    public void RelocationWithChangedMetadataIsConcurrent()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("before.txt", "content", "same-identity"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("after.txt", "content", "same-identity", lastWriteOffsetMinutes: 1));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Concurrent, result.Status);
        Assert.Equal(
            "reconcile.entry_changed_during_relocation",
            Assert.Single(result.Effects).ReasonCode);
    }

    [Fact]
    public void IdentitySharedByHardLinksIsNotTreatedAsUniqueMoveEvidence()
    {
        FolderSnapshotEntry first = File("first.txt", "same", "shared-identity");
        FolderSnapshotEntry second = File("second.txt", "same", "shared-identity");
        FolderSnapshot baseline = Snapshot(isFinal: false, first, second);
        FolderSnapshot final = Snapshot(
            isFinal: true,
            first,
            File("third.txt", "same", "shared-identity"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Ambiguous, result.Status);
        ReconciledFileEffect ambiguity = Assert.Single(result.Effects);
        Assert.Equal("reconcile.non_unique_entry_identity", ambiguity.ReasonCode);
        Assert.Equal(["first.txt", "second.txt"], ambiguity.CandidateSourcePaths);
    }

    [Fact]
    public void MultipleIdenticalCopySourcesRemainTypedAmbiguity()
    {
        FolderSnapshotEntry first = File("one.txt", "same", "one");
        FolderSnapshotEntry second = File("two.txt", "same", "two");
        FolderSnapshot baseline = Snapshot(isFinal: false, first, second);
        FolderSnapshot final = Snapshot(
            isFinal: true,
            first,
            second,
            File("copy.txt", "same", "copy"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Ambiguous, result.Status);
        ReconciledFileEffect effect = Assert.Single(result.Effects);
        Assert.Equal(ReconciledEffectKind.Ambiguous, effect.Kind);
        Assert.Equal(["one.txt", "two.txt"], effect.CandidateSourcePaths);
    }

    [Fact]
    public void MatchingRemovedAndAddedFingerprintsNeedIdentityOrRenameHint()
    {
        FolderSnapshot baseline = Snapshot(
            isFinal: false,
            File("before.txt", "content", "before-identity"));
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("after.txt", "content", "after-identity"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Ambiguous, result.Status);
        Assert.Equal(
            "reconcile.fingerprint_correlation_needs_hint",
            Assert.Single(result.Effects).ReasonCode);
    }

    [Fact]
    public void NewFileIsSurfacedAsCreatedButRejectedByClosedPrimitiveSet()
    {
        FolderSnapshot baseline = Snapshot(isFinal: false);
        FolderSnapshot final = Snapshot(
            isFinal: true,
            File("new.txt", "content", "new"));

        SnapshotReconciliation result = SnapshotReconciler.Reconcile(
            baseline,
            final,
            WatcherHintBatch.Empty);

        Assert.Equal(SnapshotReconciliationStatus.Unsupported, result.Status);
        Assert.Equal(ReconciledEffectKind.Created, Assert.Single(result.Effects).Kind);
        Assert.False(result.Effects[0].IsSupportedForCompilation);
    }

    private static string[] Project(SnapshotReconciliation result) =>
        result.Effects.Select(effect =>
                string.Join(
                    '|',
                    effect.Kind,
                    effect.SourceRelativePath,
                    effect.DestinationRelativePath,
                    effect.ReasonCode,
                    string.Join(',', effect.CandidateSourcePaths)))
            .ToArray();

    private static FolderSnapshot Snapshot(
        bool isFinal,
        params FolderSnapshotEntry[] entries)
    {
        long hashedBytes = entries
            .Where(static entry => entry.ContentHashStatus == SnapshotContentHashStatus.Computed)
            .Sum(static entry => entry.Length!.Value);
        DateTimeOffset started = isFinal ? Now.AddMinutes(2) : Now;
        return new FolderSnapshot(
            new ResourceRootIdentity("reconciliation-root"),
            started,
            started.AddSeconds(1),
            FolderSnapshotStatus.Complete,
            reasonCode: null,
            hashedBytes,
            entries.OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase));
    }

    private static FolderSnapshotEntry File(
        string relativePath,
        string content,
        string identity,
        int lastWriteOffsetMinutes = 0)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new FolderSnapshotEntry(
            relativePath,
            SnapshotEntryKind.File,
            bytes.Length,
            Now.AddMinutes(-1),
            Now.AddMinutes(lastWriteOffsetMinutes),
            FileAttributes.Archive,
            isReparsePoint: false,
            "volume",
            identity,
            SnapshotContentHashStatus.Computed,
            new ContentHash(Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private static FolderSnapshotEntry Directory(string relativePath, string identity) =>
        new(
            relativePath,
            SnapshotEntryKind.Directory,
            length: null,
            Now.AddMinutes(-1),
            Now,
            FileAttributes.Directory,
            isReparsePoint: false,
            "volume",
            identity,
            SnapshotContentHashStatus.NotApplicable,
            contentHash: null);
}

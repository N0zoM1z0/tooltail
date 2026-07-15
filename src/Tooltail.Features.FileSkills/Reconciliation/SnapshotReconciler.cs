using Tooltail.Features.FileSkills.Observation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Reconciliation;

public static class SnapshotReconciler
{
    public static SnapshotReconciliation Reconcile(
        FolderSnapshot baseline,
        FolderSnapshot final,
        WatcherHintBatch watcherHints)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(watcherHints);

        if (!baseline.IsComplete || !final.IsComplete)
        {
            return Result(
                SnapshotReconciliationStatus.IncompleteSnapshot,
                "reconcile.snapshot_incomplete");
        }

        if (baseline.RootIdentity != final.RootIdentity)
        {
            return Result(
                SnapshotReconciliationStatus.RootMismatch,
                "reconcile.root_mismatch");
        }

        if (final.StartedUtc < baseline.CompletedUtc)
        {
            return Result(
                SnapshotReconciliationStatus.Concurrent,
                "reconcile.snapshot_lifecycles_overlap");
        }

        if (watcherHints.Overflowed)
        {
            return Result(
                SnapshotReconciliationStatus.WatcherOverflow,
                "reconcile.watcher_overflow");
        }

        if (watcherHints.SourceFaulted)
        {
            return Result(
                SnapshotReconciliationStatus.WatcherFault,
                "reconcile.watcher_fault");
        }

        if (watcherHints.DroppedHintCount > 0)
        {
            return Result(
                SnapshotReconciliationStatus.WatcherOverflow,
                "reconcile.watcher_overflow");
        }

        if (!watcherHints.Quiesced)
        {
            return Result(
                SnapshotReconciliationStatus.WatcherNotQuiesced,
                "reconcile.watcher_not_quiesced");
        }

        if (!TryIndex(baseline.Entries, out Dictionary<string, FolderSnapshotEntry>? beforeByPath) ||
            !TryIndex(final.Entries, out Dictionary<string, FolderSnapshotEntry>? afterByPath))
        {
            return Result(
                SnapshotReconciliationStatus.Ambiguous,
                "reconcile.case_conflicting_paths");
        }

        List<ReconciledFileEffect> effects = [];
        HashSet<string> affectedCommonPaths = new(StringComparer.OrdinalIgnoreCase);
        ReconcileCommonEntries(beforeByPath, afterByPath, effects, affectedCommonPaths);

        List<FolderSnapshotEntry> removed = beforeByPath.Values
            .Where(entry => !afterByPath.ContainsKey(entry.RelativePath))
            .OrderBy(static entry => entry.RelativePath, SnapshotPathComparer.Instance)
            .ToList();
        List<FolderSnapshotEntry> added = afterByPath.Values
            .Where(entry => !beforeByPath.ContainsKey(entry.RelativePath))
            .OrderBy(static entry => entry.RelativePath, SnapshotPathComparer.Instance)
            .ToList();
        HashSet<string> matchedRemoved = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> matchedAdded = new(StringComparer.OrdinalIgnoreCase);

        ReconcileStableIdentities(
            beforeByPath.Values,
            afterByPath.Values,
            removed,
            added,
            matchedRemoved,
            matchedAdded,
            effects);
        ReconcileHintedMoves(
            removed,
            added,
            watcherHints.Hints,
            matchedRemoved,
            matchedAdded,
            effects);
        ReconcileUnresolvedFingerprintGroups(
            removed,
            added,
            matchedRemoved,
            matchedAdded,
            effects);
        ReconcileCopiesAndCreations(
            beforeByPath,
            afterByPath,
            affectedCommonPaths,
            added,
            matchedAdded,
            effects);
        ReconcileDeletions(removed, matchedRemoved, effects);

        effects.Sort(ReconciledEffectComparer.Instance);
        if (effects.Count == 0)
        {
            return Result(
                SnapshotReconciliationStatus.Ambiguous,
                "reconcile.no_effects");
        }

        SnapshotReconciliationStatus status = DeriveStatus(effects);
        return Result(status, StatusReasonCode(status), effects);
    }

    private static void ReconcileCommonEntries(
        Dictionary<string, FolderSnapshotEntry> beforeByPath,
        Dictionary<string, FolderSnapshotEntry> afterByPath,
        List<ReconciledFileEffect> effects,
        HashSet<string> affectedPaths)
    {
        foreach (FolderSnapshotEntry before in beforeByPath.Values.OrderBy(
                     static entry => entry.RelativePath,
                     SnapshotPathComparer.Instance))
        {
            if (!afterByPath.TryGetValue(before.RelativePath, out FolderSnapshotEntry? after))
            {
                continue;
            }

            if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.Ordinal))
            {
                effects.Add(
                    DiagnosticEffect(
                        ReconciledEffectKind.Unsupported,
                        before,
                        after,
                        "reconcile.case_only_rename_unsupported"));
                affectedPaths.Add(before.RelativePath);
                continue;
            }

            if (before.IsReparsePoint || after.IsReparsePoint)
            {
                effects.Add(
                    DiagnosticEffect(
                        ReconciledEffectKind.Unsupported,
                        before,
                        after,
                        "reconcile.reparse_point_unsupported"));
                affectedPaths.Add(before.RelativePath);
                continue;
            }

            if (IdentityShapeChanged(before, after))
            {
                effects.Add(
                    DiagnosticEffect(
                        ReconciledEffectKind.Concurrent,
                        before,
                        after,
                        "reconcile.entry_replaced_at_same_path"));
                affectedPaths.Add(before.RelativePath);
                continue;
            }

            if (before.Kind == SnapshotEntryKind.Other)
            {
                effects.Add(
                    DiagnosticEffect(
                        ReconciledEffectKind.Unsupported,
                        before,
                        after,
                        "reconcile.entry_kind_unsupported"));
                affectedPaths.Add(before.RelativePath);
                continue;
            }

            if (before.Kind == SnapshotEntryKind.File && FileEvidenceChanged(before, after))
            {
                effects.Add(
                    new ReconciledFileEffect(
                        ReconciledEffectKind.Modified,
                        before.RelativePath,
                        after.RelativePath,
                        before,
                        after,
                        "reconcile.file_modified"));
                affectedPaths.Add(before.RelativePath);
                continue;
            }

            if (before.Kind == SnapshotEntryKind.Directory &&
                before.Attributes != after.Attributes)
            {
                effects.Add(
                    DiagnosticEffect(
                        ReconciledEffectKind.Unsupported,
                        before,
                        after,
                        "reconcile.directory_metadata_changed"));
                affectedPaths.Add(before.RelativePath);
            }
        }
    }

    private static void ReconcileStableIdentities(
        IEnumerable<FolderSnapshotEntry> allBefore,
        IEnumerable<FolderSnapshotEntry> allAfter,
        IReadOnlyCollection<FolderSnapshotEntry> removed,
        IReadOnlyCollection<FolderSnapshotEntry> added,
        HashSet<string> matchedRemoved,
        HashSet<string> matchedAdded,
        List<ReconciledFileEffect> effects)
    {
        Dictionary<EntryIdentityKey, List<FolderSnapshotEntry>> removedByIdentity =
            GroupByIdentity(removed);
        Dictionary<EntryIdentityKey, List<FolderSnapshotEntry>> addedByIdentity =
            GroupByIdentity(added);
        Dictionary<EntryIdentityKey, List<FolderSnapshotEntry>> allBeforeByIdentity =
            GroupByIdentity(allBefore);
        Dictionary<EntryIdentityKey, List<FolderSnapshotEntry>> allAfterByIdentity =
            GroupByIdentity(allAfter);

        foreach (EntryIdentityKey key in removedByIdentity.Keys
                     .Intersect(addedByIdentity.Keys)
                     .OrderBy(static key => key.VolumeIdentity, StringComparer.Ordinal)
                     .ThenBy(static key => key.EntryIdentity, StringComparer.Ordinal)
                     .ThenBy(static key => key.Kind))
        {
            List<FolderSnapshotEntry> sources = removedByIdentity[key];
            List<FolderSnapshotEntry> destinations = addedByIdentity[key];
            if (sources.Count == 1 &&
                destinations.Count == 1 &&
                allBeforeByIdentity[key].Count == 1 &&
                allAfterByIdentity[key].Count == 1)
            {
                FolderSnapshotEntry before = sources[0];
                FolderSnapshotEntry after = destinations[0];
                matchedRemoved.Add(before.RelativePath);
                matchedAdded.Add(after.RelativePath);
                if (!EntriesCompatibleForRelocation(before, after))
                {
                    effects.Add(
                        DiagnosticEffect(
                            ReconciledEffectKind.Concurrent,
                            before,
                            after,
                            "reconcile.entry_changed_during_relocation"));
                    continue;
                }

                effects.Add(RelocationEffect(before, after, "reconcile.stable_identity_match"));
                continue;
            }

            string[] candidatePaths = allBeforeByIdentity[key]
                .Select(static entry => entry.RelativePath)
                .OrderBy(static path => path, SnapshotPathComparer.Instance)
                .ToArray();
            foreach (FolderSnapshotEntry source in sources)
            {
                matchedRemoved.Add(source.RelativePath);
            }

            foreach (FolderSnapshotEntry destination in destinations)
            {
                matchedAdded.Add(destination.RelativePath);
                effects.Add(
                    new ReconciledFileEffect(
                        ReconciledEffectKind.Ambiguous,
                        sourceRelativePath: null,
                        destination.RelativePath,
                        before: null,
                        destination,
                        "reconcile.non_unique_entry_identity",
                        candidatePaths));
            }
        }
    }

    private static void ReconcileHintedMoves(
        IReadOnlyCollection<FolderSnapshotEntry> removed,
        IReadOnlyCollection<FolderSnapshotEntry> added,
        IEnumerable<WatcherHint> hints,
        HashSet<string> matchedRemoved,
        HashSet<string> matchedAdded,
        List<ReconciledFileEffect> effects)
    {
        Dictionary<string, FolderSnapshotEntry> remainingRemoved = removed
            .Where(entry => !matchedRemoved.Contains(entry.RelativePath))
            .ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FolderSnapshotEntry> remainingAdded = added
            .Where(entry => !matchedAdded.Contains(entry.RelativePath))
            .ToDictionary(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        Correlation[] correlations = hints
            .Where(static hint => hint.Kind == WatcherHintKind.Renamed)
            .Select(hint => new Correlation(hint.OldRelativePath!, hint.RelativePath))
            .Distinct(CorrelationComparer.Instance)
            .Where(correlation =>
                remainingRemoved.TryGetValue(correlation.Source, out FolderSnapshotEntry? before) &&
                remainingAdded.TryGetValue(correlation.Destination, out FolderSnapshotEntry? after) &&
                EntriesHaveStrongMatchingFingerprint(before, after))
            .OrderBy(static correlation => correlation.Source, SnapshotPathComparer.Instance)
            .ThenBy(static correlation => correlation.Destination, SnapshotPathComparer.Instance)
            .ToArray();

        foreach (IGrouping<string, Correlation> destinationGroup in correlations.GroupBy(
                     static correlation => correlation.Destination,
                     StringComparer.OrdinalIgnoreCase))
        {
            Correlation[] destinationCandidates = destinationGroup.ToArray();
            Correlation correlation = destinationCandidates[0];
            int sourceUseCount = correlations.Count(candidate =>
                string.Equals(candidate.Source, correlation.Source, StringComparison.OrdinalIgnoreCase));
            if (destinationCandidates.Length == 1 && sourceUseCount == 1)
            {
                FolderSnapshotEntry before = remainingRemoved[correlation.Source];
                FolderSnapshotEntry after = remainingAdded[correlation.Destination];
                matchedRemoved.Add(before.RelativePath);
                matchedAdded.Add(after.RelativePath);
                effects.Add(RelocationEffect(before, after, "reconcile.watcher_hint_disambiguated"));
                continue;
            }

            FolderSnapshotEntry destination = remainingAdded[correlation.Destination];
            string[] candidatePaths = destinationCandidates
                .Select(static candidate => candidate.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            matchedAdded.Add(destination.RelativePath);
            foreach (string source in candidatePaths)
            {
                matchedRemoved.Add(source);
            }

            effects.Add(
                new ReconciledFileEffect(
                    ReconciledEffectKind.Ambiguous,
                    sourceRelativePath: null,
                    destination.RelativePath,
                    before: null,
                    destination,
                    "reconcile.conflicting_rename_hints",
                    candidatePaths));
        }
    }

    private static void ReconcileUnresolvedFingerprintGroups(
        IReadOnlyCollection<FolderSnapshotEntry> removed,
        IReadOnlyCollection<FolderSnapshotEntry> added,
        HashSet<string> matchedRemoved,
        HashSet<string> matchedAdded,
        List<ReconciledFileEffect> effects)
    {
        Dictionary<ContentFingerprintKey, List<FolderSnapshotEntry>> removedGroups =
            GroupByContentFingerprint(removed.Where(entry => !matchedRemoved.Contains(entry.RelativePath)));
        Dictionary<ContentFingerprintKey, List<FolderSnapshotEntry>> addedGroups =
            GroupByContentFingerprint(added.Where(entry => !matchedAdded.Contains(entry.RelativePath)));

        foreach (ContentFingerprintKey key in removedGroups.Keys
                     .Intersect(addedGroups.Keys)
                     .OrderBy(static key => key.Hash, StringComparer.Ordinal)
                     .ThenBy(static key => key.Length))
        {
            List<FolderSnapshotEntry> sources = removedGroups[key];
            List<FolderSnapshotEntry> destinations = addedGroups[key];
            string[] candidatePaths = sources.Select(static entry => entry.RelativePath).ToArray();
            foreach (FolderSnapshotEntry source in sources)
            {
                matchedRemoved.Add(source.RelativePath);
            }

            foreach (FolderSnapshotEntry destination in destinations)
            {
                matchedAdded.Add(destination.RelativePath);
                effects.Add(
                    new ReconciledFileEffect(
                        ReconciledEffectKind.Ambiguous,
                        sourceRelativePath: null,
                        destination.RelativePath,
                        before: null,
                        destination,
                        "reconcile.fingerprint_correlation_needs_hint",
                        candidatePaths));
            }
        }
    }

    private static void ReconcileCopiesAndCreations(
        Dictionary<string, FolderSnapshotEntry> beforeByPath,
        Dictionary<string, FolderSnapshotEntry> afterByPath,
        HashSet<string> affectedCommonPaths,
        IEnumerable<FolderSnapshotEntry> added,
        HashSet<string> matchedAdded,
        List<ReconciledFileEffect> effects)
    {
        FolderSnapshotEntry[] unchangedSources = beforeByPath.Values
            .Where(before =>
                !affectedCommonPaths.Contains(before.RelativePath) &&
                before.Kind == SnapshotEntryKind.File &&
                !before.IsReparsePoint &&
                before.ContentHashStatus == SnapshotContentHashStatus.Computed &&
                afterByPath.TryGetValue(before.RelativePath, out FolderSnapshotEntry? after) &&
                EntriesHaveStrongMatchingFingerprint(before, after))
            .OrderBy(static entry => entry.RelativePath, SnapshotPathComparer.Instance)
            .ToArray();

        foreach (FolderSnapshotEntry destination in added
                     .Where(entry => !matchedAdded.Contains(entry.RelativePath))
                     .OrderBy(static entry => entry.RelativePath, SnapshotPathComparer.Instance))
        {
            matchedAdded.Add(destination.RelativePath);
            if (destination.IsReparsePoint)
            {
                effects.Add(
                    DiagnosticEffect(
                        ReconciledEffectKind.Unsupported,
                        before: null,
                        destination,
                        "reconcile.reparse_point_unsupported"));
                continue;
            }

            if (TryContentFingerprint(destination, out ContentFingerprintKey destinationKey))
            {
                FolderSnapshotEntry[] candidates = unchangedSources
                    .Where(source =>
                        TryContentFingerprint(source, out ContentFingerprintKey sourceKey) &&
                        sourceKey == destinationKey)
                    .ToArray();
                if (candidates.Length == 1)
                {
                    FolderSnapshotEntry source = candidates[0];
                    effects.Add(
                        new ReconciledFileEffect(
                            ReconciledEffectKind.Copied,
                            source.RelativePath,
                            destination.RelativePath,
                            source,
                            destination,
                            "reconcile.unique_content_source"));
                    continue;
                }

                if (candidates.Length > 1)
                {
                    effects.Add(
                        new ReconciledFileEffect(
                            ReconciledEffectKind.Ambiguous,
                            sourceRelativePath: null,
                            destination.RelativePath,
                            before: null,
                            destination,
                            "reconcile.copy_source_ambiguous",
                            candidates.Select(static entry => entry.RelativePath)));
                    continue;
                }
            }

            effects.Add(
                new ReconciledFileEffect(
                    ReconciledEffectKind.Created,
                    sourceRelativePath: null,
                    destination.RelativePath,
                    before: null,
                    destination,
                    destination.Kind == SnapshotEntryKind.Directory
                        ? "reconcile.directory_created"
                        : "reconcile.file_creation_unsupported"));
        }
    }

    private static void ReconcileDeletions(
        IEnumerable<FolderSnapshotEntry> removed,
        HashSet<string> matchedRemoved,
        List<ReconciledFileEffect> effects)
    {
        foreach (FolderSnapshotEntry source in removed
                     .Where(entry => !matchedRemoved.Contains(entry.RelativePath))
                     .OrderBy(static entry => entry.RelativePath, SnapshotPathComparer.Instance))
        {
            matchedRemoved.Add(source.RelativePath);
            effects.Add(
                new ReconciledFileEffect(
                    ReconciledEffectKind.Deleted,
                    source.RelativePath,
                    destinationRelativePath: null,
                    source,
                    after: null,
                    source.IsReparsePoint
                        ? "reconcile.reparse_point_unsupported"
                        : "reconcile.deletion_unsupported"));
        }
    }

    private static Dictionary<EntryIdentityKey, List<FolderSnapshotEntry>> GroupByIdentity(
        IEnumerable<FolderSnapshotEntry> entries) =>
        entries
            .Where(static entry =>
                !entry.IsReparsePoint &&
                entry.VolumeIdentity is not null &&
                entry.EntryIdentity is not null)
            .GroupBy(static entry => new EntryIdentityKey(
                entry.VolumeIdentity!,
                entry.EntryIdentity!,
                entry.Kind))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(entry => entry.RelativePath, SnapshotPathComparer.Instance)
                    .ToList());

    private static Dictionary<ContentFingerprintKey, List<FolderSnapshotEntry>>
        GroupByContentFingerprint(IEnumerable<FolderSnapshotEntry> entries) =>
        entries
            .Select(entry => (Entry: entry, Success: TryContentFingerprint(entry, out ContentFingerprintKey key), Key: key))
            .Where(static item => item.Success)
            .GroupBy(static item => item.Key, static item => item.Entry)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(entry => entry.RelativePath, SnapshotPathComparer.Instance)
                    .ToList());

    private static bool TryContentFingerprint(
        FolderSnapshotEntry entry,
        out ContentFingerprintKey key)
    {
        if (entry is
            {
                Kind: SnapshotEntryKind.File,
                IsReparsePoint: false,
                ContentHashStatus: SnapshotContentHashStatus.Computed,
                ContentHash: not null,
                Length: not null,
            })
        {
            key = new ContentFingerprintKey(entry.ContentHash.Value, entry.Length.Value);
            return true;
        }

        key = default;
        return false;
    }

    private static bool EntriesHaveStrongMatchingFingerprint(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after)
    {
        if (before.Kind != after.Kind || before.IsReparsePoint || after.IsReparsePoint)
        {
            return false;
        }

        if (before.Kind == SnapshotEntryKind.Directory)
        {
            return before.EntryIdentity is not null &&
                string.Equals(before.VolumeIdentity, after.VolumeIdentity, StringComparison.Ordinal) &&
                string.Equals(before.EntryIdentity, after.EntryIdentity, StringComparison.Ordinal);
        }

        return TryContentFingerprint(before, out ContentFingerprintKey beforeKey) &&
            TryContentFingerprint(after, out ContentFingerprintKey afterKey) &&
            beforeKey == afterKey &&
            before.CreationUtc == after.CreationUtc &&
            before.LastWriteUtc == after.LastWriteUtc &&
            before.Attributes == after.Attributes;
    }

    private static bool EntriesCompatibleForRelocation(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after)
    {
        if (before.Kind != after.Kind || before.IsReparsePoint || after.IsReparsePoint)
        {
            return false;
        }

        if (before.Kind == SnapshotEntryKind.Directory)
        {
            return true;
        }

        if (before.Kind != SnapshotEntryKind.File ||
            before.Length != after.Length ||
            before.CreationUtc != after.CreationUtc ||
            before.LastWriteUtc != after.LastWriteUtc ||
            before.Attributes != after.Attributes)
        {
            return false;
        }

        if (before.ContentHashStatus == SnapshotContentHashStatus.Computed &&
            after.ContentHashStatus == SnapshotContentHashStatus.Computed)
        {
            return before.ContentHash == after.ContentHash;
        }

        return before.LastWriteUtc == after.LastWriteUtc;
    }

    private static bool IdentityShapeChanged(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after)
    {
        if (before.Kind != after.Kind ||
            (before.EntryIdentity is null) != (after.EntryIdentity is null))
        {
            return true;
        }

        return before.EntryIdentity is not null &&
            (!string.Equals(before.VolumeIdentity, after.VolumeIdentity, StringComparison.Ordinal) ||
             !string.Equals(before.EntryIdentity, after.EntryIdentity, StringComparison.Ordinal));
    }

    private static bool FileEvidenceChanged(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after)
    {
        if (before.Length != after.Length ||
            before.CreationUtc != after.CreationUtc ||
            before.LastWriteUtc != after.LastWriteUtc ||
            before.Attributes != after.Attributes ||
            before.ContentHashStatus != after.ContentHashStatus)
        {
            return true;
        }

        return before.ContentHashStatus == SnapshotContentHashStatus.Computed &&
            before.ContentHash != after.ContentHash;
    }

    private static ReconciledFileEffect RelocationEffect(
        FolderSnapshotEntry before,
        FolderSnapshotEntry after,
        string evidenceReason)
    {
        ReconciledEffectKind kind = string.Equals(
                ParentPath(before.RelativePath),
                ParentPath(after.RelativePath),
                StringComparison.OrdinalIgnoreCase)
            ? ReconciledEffectKind.Renamed
            : ReconciledEffectKind.Moved;
        string reason = before.Kind == SnapshotEntryKind.File
            ? evidenceReason
            : "reconcile.directory_relocation_unsupported";
        return new ReconciledFileEffect(
            kind,
            before.RelativePath,
            after.RelativePath,
            before,
            after,
            reason);
    }

    private static ReconciledFileEffect DiagnosticEffect(
        ReconciledEffectKind kind,
        FolderSnapshotEntry? before,
        FolderSnapshotEntry? after,
        string reasonCode) =>
        new(
            kind,
            before?.RelativePath,
            after?.RelativePath,
            before,
            after,
            reasonCode);

    private static string ParentPath(string path)
    {
        int separator = path.LastIndexOf('\\');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static bool TryIndex(
        IEnumerable<FolderSnapshotEntry> entries,
        out Dictionary<string, FolderSnapshotEntry> index)
    {
        index = new Dictionary<string, FolderSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderSnapshotEntry entry in entries)
        {
            if (!index.TryAdd(entry.RelativePath, entry))
            {
                return false;
            }
        }

        return true;
    }

    private static SnapshotReconciliationStatus DeriveStatus(
        IEnumerable<ReconciledFileEffect> effects)
    {
        ReconciledFileEffect[] materialized = effects.ToArray();
        if (materialized.Any(static effect => effect.Kind == ReconciledEffectKind.Concurrent))
        {
            return SnapshotReconciliationStatus.Concurrent;
        }

        if (materialized.Any(static effect => effect.Kind == ReconciledEffectKind.Ambiguous))
        {
            return SnapshotReconciliationStatus.Ambiguous;
        }

        return materialized.All(static effect => effect.IsSupportedForCompilation)
            ? SnapshotReconciliationStatus.Complete
            : SnapshotReconciliationStatus.Unsupported;
    }

    private static string StatusReasonCode(SnapshotReconciliationStatus status) =>
        status switch
        {
            SnapshotReconciliationStatus.Complete => "reconcile.complete",
            SnapshotReconciliationStatus.Concurrent => "reconcile.concurrent_change",
            SnapshotReconciliationStatus.Ambiguous => "reconcile.ambiguous",
            _ => "reconcile.unsupported_effect",
        };

    private static SnapshotReconciliation Result(
        SnapshotReconciliationStatus status,
        string reasonCode,
        IEnumerable<ReconciledFileEffect>? effects = null) =>
        new(status, reasonCode, effects ?? []);

    private readonly record struct EntryIdentityKey(
        string VolumeIdentity,
        string EntryIdentity,
        SnapshotEntryKind Kind);

    private readonly record struct ContentFingerprintKey(string Hash, long Length);

    private readonly record struct Correlation(string Source, string Destination);

    private sealed class CorrelationComparer : IEqualityComparer<Correlation>
    {
        public static CorrelationComparer Instance { get; } = new();

        public bool Equals(Correlation left, Correlation right) =>
            string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Destination, right.Destination, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(Correlation value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Destination));
    }

    private sealed class SnapshotPathComparer : IComparer<string>
    {
        public static SnapshotPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
        }
    }

    private sealed class ReconciledEffectComparer : IComparer<ReconciledFileEffect>
    {
        public static ReconciledEffectComparer Instance { get; } = new();

        public int Compare(ReconciledFileEffect? left, ReconciledFileEffect? right)
        {
            int kind = Nullable.Compare(left?.Kind, right?.Kind);
            if (kind != 0)
            {
                return kind;
            }

            int source = SnapshotPathComparer.Instance.Compare(
                left?.SourceRelativePath,
                right?.SourceRelativePath);
            return source != 0
                ? source
                : SnapshotPathComparer.Instance.Compare(
                    left?.DestinationRelativePath,
                    right?.DestinationRelativePath);
        }
    }
}

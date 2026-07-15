using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Execution;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Planning;

public sealed class SkillPlanner
{
    private readonly SkillPlanningLimits limits;

    public SkillPlanner(SkillPlanningLimits? limits = null) =>
        this.limits = limits ?? SkillPlanningLimits.Default;

    public SkillPlanningResult DryRun(SkillPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillValidationResult validation = SkillSpecValidator.Validate(request.Specification);
        if (!validation.IsValid)
        {
            return Failure(
                SkillPlanningStatus.InvalidSkill,
                matchedFileCount: 0,
                validation.Errors.Select(static error => new SkillPlanningDiagnostic(
                    error.Code,
                    error.Field,
                    error.Message)));
        }

        SkillPlanningResult? requestFailure = ValidateRequest(request);
        if (requestFailure is not null)
        {
            return requestFailure;
        }

        if (!TryIndexSnapshot(
                request.Snapshot,
                out Dictionary<string, FolderSnapshotEntry>? entries,
                out SkillPlanningDiagnostic? indexError))
        {
            return Failure(SkillPlanningStatus.Conflict, 0, indexError!);
        }

        FolderSnapshotEntry[] matches = request.Snapshot.Entries
            .Where(entry => SkillMatcher.Matches(
                request.Specification.Applicability.Match,
                entry))
            .OrderBy(static entry => entry.RelativePath, WindowsPathComparer.Instance)
            .ToArray();
        if (matches.Length == 0)
        {
            return Failure(
                SkillPlanningStatus.NoMatches,
                0,
                Diagnostic(
                    "planner.no_matches",
                    "snapshot.entries",
                    "No regular snapshot file matches this exact SkillSpec."));
        }

        if (matches.Length > limits.MaximumMatches)
        {
            return Failure(
                SkillPlanningStatus.LimitExceeded,
                matches.Length,
                Diagnostic(
                    "planner.match_limit_exceeded",
                    "snapshot.entries",
                    "The number of matching files exceeds the bounded planning limit."));
        }

        if (matches.Any(static entry =>
                entry.ContentHashStatus == SnapshotContentHashStatus.Computed) &&
            !request.Grant.Allows(GrantCapability.ReadContentHash, request.CreatedUtc))
        {
            return Failure(
                SkillPlanningStatus.AuthorityDenied,
                matches.Length,
                Diagnostic(
                    "planner.hash_authority_missing",
                    "grant.capabilities",
                    "Hashed source evidence requires the matching content-hash grant capability."));
        }

        List<SkillPlanningDiagnostic> diagnostics = [];
        List<RenderedFileDraft> drafts = RenderDrafts(request, matches, entries!, diagnostics);
        if (diagnostics.Count > 0)
        {
            return Failure(
                SkillPlanningStatus.Conflict,
                matches.Length,
                diagnostics);
        }

        Dictionary<string, string> plannedDirectories =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (RenderedFileDraft draft in drafts)
        {
            foreach (string target in draft.EnsureDirectories
                         .Order(WindowsPathComparer.Instance))
            {
                AddEnsureDirectory(
                    target,
                    draft.Source.RelativePath,
                    entries!,
                    plannedDirectories,
                    diagnostics);
            }
        }

        if (diagnostics.Count > 0)
        {
            return Failure(
                SkillPlanningStatus.Conflict,
                matches.Length,
                diagnostics);
        }

        List<ResolvedFileDraft> resolved = ResolveFileDestinations(
            drafts,
            entries!,
            plannedDirectories,
            diagnostics);
        if (diagnostics.Count > 0)
        {
            return Failure(
                SkillPlanningStatus.Conflict,
                matches.Length,
                diagnostics);
        }

        int operationCount;
        try
        {
            operationCount = checked(plannedDirectories.Count + resolved.Count);
        }
        catch (OverflowException)
        {
            operationCount = int.MaxValue;
        }

        if (operationCount > limits.MaximumOperations)
        {
            return Failure(
                SkillPlanningStatus.LimitExceeded,
                matches.Length,
                Diagnostic(
                    "planner.operation_limit_exceeded",
                    "plan.operations",
                    "The exact operation list exceeds the bounded planning limit."));
        }

        PlannedFileOperation[] operations = CreateOperations(plannedDirectories, resolved);
        ExecutionPlanDefinition definition = new(
            request.PlanId,
            new SkillId(request.Specification.SkillId),
            new SkillVersionNumber(request.Specification.Version),
            request.SpecificationHash,
            request.Grant.Id,
            request.Grant.RootIdentity,
            request.Grant.Capabilities,
            request.CreatedUtc,
            request.ExpiresUtc,
            operations);
        PathSafetyResult<ExecutionPlan> exactPlan = CanonicalExecutionPlan.Create(definition);
        if (!exactPlan.IsSuccess)
        {
            return Failure(
                SkillPlanningStatus.Conflict,
                matches.Length,
                Diagnostic(
                    exactPlan.Error!.Code,
                    "plan.operations",
                    exactPlan.Error.Message));
        }

        return new SkillPlanningResult(
            SkillPlanningStatus.Ready,
            exactPlan.Value,
            matches.Length,
            diagnostics: []);
    }

    private SkillPlanningResult? ValidateRequest(SkillPlanningRequest request)
    {
        if (request.PlanId.Value == Guid.Empty)
        {
            return Failure(
                SkillPlanningStatus.InvalidRequest,
                0,
                Diagnostic(
                    "planner.plan_id_empty",
                    "planId",
                    "An exact non-empty plan identifier is required."));
        }

        if (request.CreatedUtc.Offset != TimeSpan.Zero ||
            request.ExpiresUtc.Offset != TimeSpan.Zero ||
            request.ExpiresUtc <= request.CreatedUtc ||
            request.ExpiresUtc - request.CreatedUtc > limits.MaximumPlanLifetime)
        {
            return Failure(
                SkillPlanningStatus.InvalidRequest,
                0,
                Diagnostic(
                    "planner.plan_lifetime_invalid",
                    "expiresUtc",
                    "The plan lifetime must be bounded, increasing, and expressed in UTC."));
        }

        SkillSpecificationHash actualHash =
            CanonicalSkillSpec.ComputeHash(request.Specification);
        if (actualHash != request.SpecificationHash)
        {
            return Failure(
                SkillPlanningStatus.InvalidRequest,
                0,
                Diagnostic(
                    "planner.skill_hash_mismatch",
                    "specificationHash",
                    "The supplied SkillSpec hash does not match its canonical executable fields."));
        }

        SkillPlanningDiagnostic? parameterError = ValidateUserParameters(request);
        if (parameterError is not null)
        {
            return Failure(SkillPlanningStatus.InvalidRequest, 0, parameterError);
        }

        if (request.Specification.Applicability.RootGrantId != request.Grant.Id.Value ||
            request.Snapshot.RootIdentity != request.Grant.RootIdentity)
        {
            return Failure(
                SkillPlanningStatus.AuthorityDenied,
                0,
                Diagnostic(
                    "planner.authority_binding_mismatch",
                    "grant",
                    "The SkillSpec, snapshot, grant, and immutable root binding must agree."));
        }

        if (!request.Snapshot.IsComplete)
        {
            return Failure(
                SkillPlanningStatus.IncompleteSnapshot,
                0,
                Diagnostic(
                    "planner.snapshot_incomplete",
                    "snapshot.status",
                    "An incomplete snapshot can never produce an executable plan."));
        }

        if (request.Snapshot.StartedUtc < request.Grant.IssuedAt ||
            request.Snapshot.CompletedUtc > request.CreatedUtc ||
            request.CreatedUtc - request.Snapshot.CompletedUtc > limits.MaximumSnapshotAge)
        {
            return Failure(
                SkillPlanningStatus.InvalidRequest,
                0,
                Diagnostic(
                    "planner.snapshot_time_invalid",
                    "snapshot.completedUtc",
                    "Planning requires recent snapshot evidence captured under the current grant."));
        }

        if (request.Grant.ExpiresAt is not null &&
            request.ExpiresUtc > request.Grant.ExpiresAt.Value)
        {
            return Failure(
                SkillPlanningStatus.AuthorityDenied,
                0,
                Diagnostic(
                    "planner.plan_outlives_grant",
                    "expiresUtc",
                    "An exact plan cannot outlive its resource grant."));
        }

        foreach (GrantCapability capability in RequiredCapabilities(request.Specification))
        {
            if (!request.Grant.Allows(capability, request.CreatedUtc))
            {
                return Failure(
                    SkillPlanningStatus.AuthorityDenied,
                    0,
                    Diagnostic(
                        "planner.capability_missing",
                        "grant.capabilities",
                        $"The active resource grant does not allow {capability}."));
            }
        }

        return null;
    }

    private SkillPlanningDiagnostic? ValidateUserParameters(SkillPlanningRequest request)
    {
        string[] required = request.Specification.Variables
            .Where(static variable => variable.Source == SkillVariableSource.UserParameter)
            .Select(static variable => variable.Argument!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, string> supplied =
            request.UserParameters ?? EmptyParameters.Instance;
        if (supplied.Count > limits.MaximumUserParameters ||
            required.Length != supplied.Count ||
            required.Any(name => !supplied.ContainsKey(name)) ||
            supplied.Keys.Any(static name => string.IsNullOrWhiteSpace(name)))
        {
            return Diagnostic(
                "planner.user_parameters_mismatch",
                "userParameters",
                "User parameters must exactly match the typed SkillSpec declarations.");
        }

        if (supplied.Any(static pair =>
                pair.Value is null ||
                pair.Value.Length is 0 or > 220 ||
                pair.Value.Any(char.IsControl)))
        {
            return Diagnostic(
                "planner.user_parameter_invalid",
                "userParameters",
                "A user parameter must be a bounded non-control string.");
        }

        return null;
    }

    private static IEnumerable<GrantCapability> RequiredCapabilities(
        SkillSpecContract specification)
    {
        yield return GrantCapability.Enumerate;
        yield return GrantCapability.ReadMetadata;
        foreach (SkillStepContract step in specification.Steps)
        {
            yield return step switch
            {
                EnsureDirectoryStepContract => GrantCapability.CreateDirectory,
                RenameFileStepContract => GrantCapability.Rename,
                MoveFileStepContract => GrantCapability.MoveWithinRoot,
                CopyFileStepContract => GrantCapability.CopyWithinRoot,
                _ => throw new ArgumentOutOfRangeException(nameof(specification)),
            };
        }
    }

    private static bool TryIndexSnapshot(
        FolderSnapshot snapshot,
        out Dictionary<string, FolderSnapshotEntry>? entries,
        out SkillPlanningDiagnostic? error)
    {
        entries = new Dictionary<string, FolderSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderSnapshotEntry entry in snapshot.Entries
                     .OrderBy(static item => item.RelativePath, WindowsPathComparer.Instance))
        {
            if (!entries.TryAdd(entry.RelativePath, entry))
            {
                error = Diagnostic(
                    "planner.snapshot_path_alias",
                    "snapshot.entries",
                    "The snapshot contains paths that alias under Windows comparison.",
                    source: entry.RelativePath);
                entries = null;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static List<RenderedFileDraft> RenderDrafts(
        SkillPlanningRequest request,
        FolderSnapshotEntry[] matches,
        Dictionary<string, FolderSnapshotEntry> entries,
        List<SkillPlanningDiagnostic> diagnostics)
    {
        EnsureDirectoryStepContract[] ensureSteps = request.Specification.Steps
            .OfType<EnsureDirectoryStepContract>()
            .ToArray();
        SkillStepContract action = request.Specification.Steps
            .Single(static step => step is not EnsureDirectoryStepContract);
        List<RenderedFileDraft> drafts = new(matches.Length);
        foreach (FolderSnapshotEntry source in matches)
        {
            if (source.EntryIdentity is null || source.VolumeIdentity is null)
            {
                diagnostics.Add(Diagnostic(
                    "planner.source_identity_unavailable",
                    "snapshot.entries",
                    "A planned source needs stable platform identity evidence.",
                    source: source.RelativePath));
                continue;
            }

            ValidateExistingAncestors(source.RelativePath, entries, diagnostics);
            SkillTemplateResult binding = SkillTemplateEngine.BindVariables(
                request.Specification.Variables,
                source,
                request.Specification.Applicability.Match.Filename,
                request.UserParameters,
                out IReadOnlyDictionary<string, string>? variables);
            if (!binding.IsSuccess)
            {
                diagnostics.Add(Diagnostic(
                    binding.Error!.Code,
                    binding.Error.Field,
                    "A typed variable could not be bound from this source.",
                    source: source.RelativePath));
                continue;
            }

            List<string> ensureDirectories = [];
            foreach (EnsureDirectoryStepContract ensure in ensureSteps)
            {
                SkillTemplateResult rendered = SkillTemplateEngine.Render(
                    ensure.DirectoryTemplate,
                    SkillTemplateKind.RelativePath,
                    variables!,
                    $"steps.{ensure.StepId}.directoryTemplate");
                if (!rendered.IsSuccess)
                {
                    diagnostics.Add(Diagnostic(
                        rendered.Error!.Code,
                        rendered.Error.Field,
                        "An ensure-directory template produced an unsafe path.",
                        source: source.RelativePath));
                    continue;
                }

                ensureDirectories.Add(rendered.Value!);
            }

            if (!TryRenderAction(
                    action,
                    source,
                    variables!,
                    out FilePrimitive primitive,
                    out string? destinationDirectory,
                    out string? destinationFileName,
                    out SkillTemplateError? actionError))
            {
                diagnostics.Add(Diagnostic(
                    actionError!.Code,
                    actionError.Field,
                    "The file-action template produced an unsafe path.",
                    source: source.RelativePath));
                continue;
            }

            drafts.Add(new RenderedFileDraft(
                source,
                primitive,
                destinationDirectory!,
                destinationFileName!,
                ensureDirectories));
        }

        return drafts;
    }

    private static bool TryRenderAction(
        SkillStepContract action,
        FolderSnapshotEntry source,
        IReadOnlyDictionary<string, string> variables,
        out FilePrimitive primitive,
        out string? destinationDirectory,
        out string? destinationFileName,
        out SkillTemplateError? error)
    {
        primitive = action switch
        {
            RenameFileStepContract => FilePrimitive.RenameFile,
            MoveFileStepContract => FilePrimitive.MoveFile,
            CopyFileStepContract => FilePrimitive.CopyFile,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        destinationDirectory = primitive == FilePrimitive.RenameFile
            ? SkillTemplateEngine.Parent(source.RelativePath)
            : null;
        destinationFileName = null;
        error = null;

        string? directoryTemplate = action switch
        {
            MoveFileStepContract move => move.DestinationDirectoryTemplate,
            CopyFileStepContract copy => copy.DestinationDirectoryTemplate,
            _ => null,
        };
        if (directoryTemplate is not null)
        {
            SkillTemplateResult renderedDirectory = SkillTemplateEngine.Render(
                directoryTemplate,
                SkillTemplateKind.RelativePath,
                variables,
                $"steps.{action.StepId}.destinationDirectoryTemplate");
            if (!renderedDirectory.IsSuccess)
            {
                error = renderedDirectory.Error;
                return false;
            }

            destinationDirectory = renderedDirectory.Value;
        }

        string? fileNameTemplate = action switch
        {
            RenameFileStepContract rename => rename.DestinationFileNameTemplate,
            MoveFileStepContract move => move.DestinationFileNameTemplate,
            CopyFileStepContract copy => copy.DestinationFileNameTemplate,
            _ => null,
        };
        if (fileNameTemplate is null)
        {
            destinationFileName = SkillTemplateEngine.FileName(source.RelativePath);
            return true;
        }

        SkillTemplateResult renderedFileName = SkillTemplateEngine.Render(
            fileNameTemplate,
            SkillTemplateKind.FileName,
            variables,
            $"steps.{action.StepId}.destinationFileNameTemplate");
        if (!renderedFileName.IsSuccess)
        {
            error = renderedFileName.Error;
            return false;
        }

        destinationFileName = renderedFileName.Value;
        return true;
    }

    private static void AddEnsureDirectory(
        string target,
        string source,
        Dictionary<string, FolderSnapshotEntry> entries,
        Dictionary<string, string> plannedDirectories,
        List<SkillPlanningDiagnostic> diagnostics)
    {
        string current = string.Empty;
        foreach (string segment in target.Split('\\'))
        {
            string candidate = current.Length == 0 ? segment : $"{current}\\{segment}";
            if (entries.TryGetValue(candidate, out FolderSnapshotEntry? existing))
            {
                if (existing.Kind != SnapshotEntryKind.Directory || existing.IsReparsePoint)
                {
                    diagnostics.Add(Diagnostic(
                        existing.IsReparsePoint
                            ? "planner.reparse_ancestor"
                            : "planner.ensure_path_not_directory",
                        "steps.ensure_directory",
                        "A requested directory path is not a safe existing directory.",
                        source,
                        existing.RelativePath));
                    return;
                }

                current = existing.RelativePath;
                continue;
            }

            if (plannedDirectories.TryGetValue(candidate, out string? planned))
            {
                if (!string.Equals(candidate, planned, StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic(
                        "planner.directory_case_alias",
                        "steps.ensure_directory",
                        "Generated directory paths alias under Windows comparison.",
                        source,
                        candidate));
                    return;
                }

                current = planned;
                continue;
            }

            plannedDirectories.Add(candidate, candidate);
            current = candidate;
        }
    }

    private static List<ResolvedFileDraft> ResolveFileDestinations(
        List<RenderedFileDraft> drafts,
        Dictionary<string, FolderSnapshotEntry> entries,
        Dictionary<string, string> plannedDirectories,
        List<SkillPlanningDiagnostic> diagnostics)
    {
        Dictionary<string, string> destinations = new(StringComparer.OrdinalIgnoreCase);
        List<ResolvedFileDraft> resolved = new(drafts.Count);
        foreach (RenderedFileDraft draft in drafts)
        {
            if (!TryResolveDirectory(
                    draft.DestinationDirectory,
                    entries,
                    plannedDirectories,
                    out string? destinationDirectory,
                    out string? reasonCode))
            {
                diagnostics.Add(Diagnostic(
                    reasonCode!,
                    "steps.file.destinationDirectoryTemplate",
                    "The rendered destination parent is missing, linked, or not a directory.",
                    draft.Source.RelativePath,
                    draft.DestinationDirectory));
                continue;
            }

            string destination = destinationDirectory!.Length == 0
                ? draft.DestinationFileName
                : $"{destinationDirectory}\\{draft.DestinationFileName}";
            PathSafetyResult<WindowsRelativePath> parsedDestination =
                WindowsPathPolicy.ParseRelative(destination);
            PathSafetyResult<WindowsRelativePath> parsedSource =
                WindowsPathPolicy.ParseRelative(draft.Source.RelativePath);
            if (!parsedDestination.IsSuccess || !parsedSource.IsSuccess)
            {
                PathSafetyError pathError =
                    parsedDestination.Error ?? parsedSource.Error!;
                diagnostics.Add(Diagnostic(
                    pathError.Code,
                    "plan.operations",
                    pathError.Message,
                    draft.Source.RelativePath,
                    destination));
                continue;
            }

            PathSafetyResult<ValidatedPathPair> pair =
                WindowsPathPolicy.ValidateDistinctPair(
                    parsedSource.Value!,
                    parsedDestination.Value!);
            if (!pair.IsSuccess)
            {
                diagnostics.Add(Diagnostic(
                    pair.Error!.Code,
                    "plan.operations",
                    pair.Error.Message,
                    draft.Source.RelativePath,
                    destination));
                continue;
            }

            if (entries.TryGetValue(destination, out FolderSnapshotEntry? existingDestination))
            {
                diagnostics.Add(Diagnostic(
                    "planner.destination_exists",
                    "plan.operations",
                    "Collision policy requires every file destination to be absent.",
                    draft.Source.RelativePath,
                    existingDestination.RelativePath));
                continue;
            }

            if (plannedDirectories.ContainsKey(destination) ||
                plannedDirectories.Keys.Any(directory => IsDescendant(directory, destination)))
            {
                diagnostics.Add(Diagnostic(
                    "planner.destination_type_conflict",
                    "plan.operations",
                    "A file destination conflicts with a directory created by this plan.",
                    draft.Source.RelativePath,
                    destination));
                continue;
            }

            if (!destinations.TryAdd(destination, destination))
            {
                string first = destinations[destination];
                diagnostics.Add(Diagnostic(
                    string.Equals(first, destination, StringComparison.Ordinal)
                        ? "planner.duplicate_destination"
                        : "planner.destination_case_alias",
                    "plan.operations",
                    "Multiple matching sources resolve to the same Windows destination.",
                    draft.Source.RelativePath,
                    destination));
                continue;
            }

            resolved.Add(new ResolvedFileDraft(draft, destination));
        }

        return resolved;
    }

    private static bool TryResolveDirectory(
        string path,
        Dictionary<string, FolderSnapshotEntry> entries,
        Dictionary<string, string> plannedDirectories,
        out string? resolved,
        out string? reasonCode)
    {
        if (path.Length == 0)
        {
            resolved = string.Empty;
            reasonCode = null;
            return true;
        }

        string current = string.Empty;
        foreach (string segment in path.Split('\\'))
        {
            string candidate = current.Length == 0 ? segment : $"{current}\\{segment}";
            if (entries.TryGetValue(candidate, out FolderSnapshotEntry? existing))
            {
                if (existing.IsReparsePoint)
                {
                    resolved = null;
                    reasonCode = "planner.reparse_ancestor";
                    return false;
                }

                if (existing.Kind != SnapshotEntryKind.Directory)
                {
                    resolved = null;
                    reasonCode = "planner.parent_not_directory";
                    return false;
                }

                current = existing.RelativePath;
            }
            else if (plannedDirectories.TryGetValue(candidate, out string? planned))
            {
                current = planned;
            }
            else
            {
                resolved = null;
                reasonCode = "planner.destination_parent_missing";
                return false;
            }
        }

        resolved = current;
        reasonCode = null;
        return true;
    }

    private static void ValidateExistingAncestors(
        string source,
        Dictionary<string, FolderSnapshotEntry> entries,
        List<SkillPlanningDiagnostic> diagnostics)
    {
        string parent = SkillTemplateEngine.Parent(source);
        if (parent.Length == 0)
        {
            return;
        }

        string current = string.Empty;
        foreach (string segment in parent.Split('\\'))
        {
            current = current.Length == 0 ? segment : $"{current}\\{segment}";
            if (!entries.TryGetValue(current, out FolderSnapshotEntry? existing))
            {
                diagnostics.Add(Diagnostic(
                    "planner.snapshot_hierarchy_incomplete",
                    "snapshot.entries",
                    "A nested source is missing authoritative parent-directory evidence.",
                    source,
                    current));
                return;
            }

            if (existing.IsReparsePoint || existing.Kind != SnapshotEntryKind.Directory)
            {
                diagnostics.Add(Diagnostic(
                    existing.IsReparsePoint
                        ? "planner.reparse_ancestor"
                        : "planner.parent_not_directory",
                    "snapshot.entries",
                    "A source ancestor is linked or is not a directory.",
                    source,
                    existing.RelativePath));
                return;
            }
        }
    }

    private static PlannedFileOperation[] CreateOperations(
        Dictionary<string, string> plannedDirectories,
        List<ResolvedFileDraft> resolved)
    {
        List<PlannedFileOperation> operations = [];
        foreach (string directory in plannedDirectories.Values
                     .OrderBy(static path => path.Count(static character => character == '\\'))
                     .ThenBy(static path => path, WindowsPathComparer.Instance))
        {
            operations.Add(new PlannedFileOperation(
                operations.Count + 1,
                FilePrimitive.EnsureDirectory,
                sourceRelativePath: null,
                destinationRelativePath: directory,
                sourceFingerprint: null,
                DestinationPrecondition.Absent,
                ExpectedSourceState.NotApplicable,
                ExpectedDestinationState.DirectoryPresent));
        }

        foreach (ResolvedFileDraft draft in resolved
                     .OrderBy(static item => item.Draft.Source.RelativePath, WindowsPathComparer.Instance)
                     .ThenBy(static item => item.Destination, WindowsPathComparer.Instance))
        {
            FolderSnapshotEntry source = draft.Draft.Source;
            ContentHash? contentHash = source.ContentHashStatus == SnapshotContentHashStatus.Computed
                ? source.ContentHash
                : null;
            operations.Add(new PlannedFileOperation(
                operations.Count + 1,
                draft.Draft.Primitive,
                source.RelativePath,
                draft.Destination,
                new SourceFileFingerprint(
                    source.EntryIdentity!,
                    source.Length!.Value,
                    source.LastWriteUtc,
                    contentHash),
                DestinationPrecondition.Absent,
                draft.Draft.Primitive == FilePrimitive.CopyFile
                    ? ExpectedSourceState.Unchanged
                    : ExpectedSourceState.Absent,
                ExpectedDestinationState.FileMatchesSource));
        }

        return operations.ToArray();
    }

    private static bool IsDescendant(string candidate, string ancestor) =>
        candidate.Length > ancestor.Length &&
        candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase) &&
        candidate[ancestor.Length] == '\\';

    private static SkillPlanningResult Failure(
        SkillPlanningStatus status,
        int matchedFileCount,
        params SkillPlanningDiagnostic[] diagnostics) =>
        new(status, null, matchedFileCount, diagnostics);

    private static SkillPlanningResult Failure(
        SkillPlanningStatus status,
        int matchedFileCount,
        IEnumerable<SkillPlanningDiagnostic> diagnostics) =>
        new(status, null, matchedFileCount, diagnostics);

    private static SkillPlanningDiagnostic Diagnostic(
        string code,
        string field,
        string message,
        string? source = null,
        string? destination = null) =>
        new(code, field, message, source, destination);

    private sealed record RenderedFileDraft(
        FolderSnapshotEntry Source,
        FilePrimitive Primitive,
        string DestinationDirectory,
        string DestinationFileName,
        IReadOnlyList<string> EnsureDirectories);

    private sealed record ResolvedFileDraft(
        RenderedFileDraft Draft,
        string Destination);

    private sealed class WindowsPathComparer : IComparer<string>
    {
        public static WindowsPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
        }
    }

    private sealed class EmptyParameters : Dictionary<string, string>
    {
        public static EmptyParameters Instance { get; } = new();

        private EmptyParameters()
            : base(StringComparer.Ordinal)
        {
        }
    }
}

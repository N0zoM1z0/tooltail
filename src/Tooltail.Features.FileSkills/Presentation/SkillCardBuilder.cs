using System.Globalization;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Presentation;

public static class SkillCardBuilder
{
    public static SkillCardViewModel Build(SkillCardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireValid(request.Specification, nameof(request.Specification));
        ValidateParent(request.Specification, request.ParentSpecification);

        SkillSpecificationHash currentHash =
            CanonicalSkillSpec.ComputeHash(request.Specification);
        SkillCardEvidence[] evidence = request.Evidence
            .OrderByDescending(static item => item.ObservedUtc)
            .ThenBy(static item => item.Kind)
            .ThenBy(static item => item.ReasonCode, StringComparer.Ordinal)
            .ToArray();
        List<SkillCardFactViewModel> matchPredicates =
            BuildMatchPredicates(request.Specification.Applicability.Match);
        List<SkillCardFactViewModel> variables =
            BuildVariables(request.Specification.Variables);
        List<string> operations = BuildOperations(request.Specification.Steps);
        List<SkillCardFactViewModel> learnedFrom = BuildLearnedFrom(
            request.Specification.Provenance,
            evidence,
            currentHash);
        GrantCapability[] missingCapabilities = MissingCapabilities(request);
        SkillCardActionViewModel[] actions = BuildActions(
            request,
            evidence,
            currentHash,
            missingCapabilities);

        return new SkillCardViewModel(
            request.Specification.Name,
            PlainLanguageSummary(request.Specification),
            request.Specification.SkillId.ToString("D"),
            request.Specification.Version,
            currentHash.Value,
            request.IsDisabled
                ? $"Disabled (stored lifecycle: {request.Lifecycle})"
                : request.Lifecycle.ToString(),
            EvidenceSummary(evidence, currentHash),
            "Manual invocation only; no background or silent execution.",
            request.GrantedRootLabel,
            RelativeScope(request.Specification.Applicability.Match),
            GrantedActions(request.GrantedCapabilities, missingCapabilities),
            matchPredicates,
            variables,
            operations,
            AlwaysRules(request.Specification),
            NeverRules(),
            AskRules(),
            SuccessRules(request.Specification),
            learnedFrom,
            Compatibility(request.Specification, currentHash),
            request.Samples.Select(static sample => new SkillCardSampleViewModel(
                sample.SourceRelativePath,
                sample.DestinationRelativePath)),
            evidence.Select(item => Evidence(item, currentHash)),
            BuildSemanticDiff(request.ParentSpecification, request.Specification),
            actions);
    }

    private static void RequireValid(SkillSpecContract specification, string parameterName)
    {
        SkillValidationResult validation = SkillSpecValidator.Validate(specification);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"A Skill Card requires a semantically valid SkillSpec: {validation.Errors[0].Code}.",
                parameterName);
        }
    }

    private static void ValidateParent(
        SkillSpecContract current,
        SkillSpecContract? parent)
    {
        if (parent is null)
        {
            if (current.Provenance.ParentVersion is not null)
            {
                throw new ArgumentException(
                    "The declared parent version must be supplied for an inspectable semantic diff.");
            }

            return;
        }

        RequireValid(parent, nameof(parent));
        if (parent.SkillId != current.SkillId ||
            parent.Version >= current.Version ||
            current.Provenance.ParentVersion != parent.Version)
        {
            throw new ArgumentException(
                "The parent SkillSpec must be the declared older version of the same skill.",
                nameof(parent));
        }
    }

    private static string PlainLanguageSummary(SkillSpecContract specification)
    {
        SkillStepContract action = specification.Steps
            .Single(static step => step is not EnsureDirectoryStepContract);
        string effect = action switch
        {
            RenameFileStepContract rename =>
                $"rename each match to {DisplayPath(rename.DestinationFileNameTemplate)}",
            MoveFileStepContract move =>
                $"move each match into {DisplayPath(move.DestinationDirectoryTemplate)}",
            CopyFileStepContract copy =>
                $"copy each match into {DisplayPath(copy.DestinationDirectoryTemplate)}",
            _ => throw new ArgumentOutOfRangeException(nameof(specification)),
        };
        return $"When you invoke this skill, {effect}; every exact destination must be free.";
    }

    private static List<SkillCardFactViewModel> BuildMatchPredicates(
        SkillMatchContract match)
    {
        List<SkillCardFactViewModel> facts =
        [
            new("Entry kind", "Regular local files only"),
            new("Origin", RelativeScope(match)),
            new(
                "Extensions",
                match.Extensions is null
                    ? "Any extension"
                    : string.Join(
                        ", ",
                        match.Extensions
                            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(static value => value, StringComparer.Ordinal))),
        ];
        if (match.Filename is null)
        {
            facts.Add(new SkillCardFactViewModel("Filename", "No filename predicate"));
        }
        else
        {
            SkillFilenameMatchContract filename = match.Filename;
            AddOptional(facts, "Filename prefix", filename.Prefix);
            AddOptional(facts, "Filename suffix", filename.Suffix);
            AddOptional(facts, "Filename contains", filename.Contains);
            AddOptional(facts, "Anchored safe regex", filename.SafeRegex);
            facts.Add(new SkillCardFactViewModel(
                "Filename comparison",
                filename.CaseSensitive ? "Case-sensitive" : "Ordinal, case-insensitive"));
        }

        facts.Add(new SkillCardFactViewModel(
            "Maximum size",
            match.MaxBytes is null
                ? "No additional SkillSpec size predicate"
                : $"{match.MaxBytes.Value.ToString(CultureInfo.InvariantCulture)} bytes"));
        return facts;
    }

    private static List<SkillCardFactViewModel> BuildVariables(
        IReadOnlyList<SkillVariableContract> variables)
    {
        if (variables.Count == 0)
        {
            return [new SkillCardFactViewModel("Variables", "None")];
        }

        return variables
            .OrderBy(static variable => variable.Name, StringComparer.Ordinal)
            .Select(variable => new SkillCardFactViewModel(
                variable.Name,
                $"{FormatVariableSource(variable)}; transforms: {FormatTransforms(variable.Transforms)}"))
            .ToList();
    }

    private static List<string> BuildOperations(IReadOnlyList<SkillStepContract> steps) =>
        steps.Select((step, index) =>
            $"{index + 1}. {step switch
            {
                EnsureDirectoryStepContract ensure =>
                    $"Ensure directory is present: {DisplayPath(ensure.DirectoryTemplate)}",
                RenameFileStepContract rename =>
                    $"Rename file to: {DisplayPath(rename.DestinationFileNameTemplate)}",
                MoveFileStepContract move =>
                    $"Move file to: {DisplayPath(move.DestinationDirectoryTemplate)}\\{DisplayPath(move.DestinationFileNameTemplate ?? "{{original filename}}")}",
                CopyFileStepContract copy =>
                    $"Copy file to: {DisplayPath(copy.DestinationDirectoryTemplate)}\\{DisplayPath(copy.DestinationFileNameTemplate ?? "{{original filename}}")}",
                _ => throw new ArgumentOutOfRangeException(nameof(steps)),
            }}")
            .ToList();

    private static IEnumerable<string> AlwaysRules(SkillSpecContract specification)
    {
        yield return "Bind planning and approval to this exact SkillSpec version, grant, root, inputs, operation order, and fingerprint.";
        yield return "Require every file destination to be absent; never pick a replacement name automatically.";
        yield return "Stay inside one local granted root and the same volume.";
        if (specification.Verification.AllPlannedStepsVerified)
        {
            yield return "Verify every planned step and its declared postconditions.";
        }
    }

    private static IEnumerable<string> NeverRules()
    {
        yield return "Never overwrite, merge, silently suffix, or convert one primitive into another.";
        yield return "Never follow network, UNC, device, alternate-stream, link, or reparse paths.";
        yield return "Never delete learned targets, edit file contents, run a shell/script, or automate arbitrary UI.";
        yield return "Never execute in the background or treat a WindowLease as file authority.";
    }

    private static IEnumerable<string> AskRules()
    {
        yield return "Stop for a destination collision, missing variable, changed input, stale plan, or lost authority.";
        yield return "Localize inference ambiguity to at most two typed fields; request more examples if ambiguity remains.";
    }

    private static IEnumerable<string> SuccessRules(SkillSpecContract specification)
    {
        if (specification.Verification.AllPlannedStepsVerified)
        {
            yield return "Every exact planned postcondition is verified.";
        }

        if (specification.Verification.FailOnUnexpectedChange)
        {
            yield return "No unexpected in-scope change is observed.";
        }

        yield return "A durable receipt can identify the exact plan and verification result.";
    }

    private static List<SkillCardFactViewModel> BuildLearnedFrom(
        SkillProvenanceContract provenance,
        IReadOnlyList<SkillCardEvidence> evidence,
        SkillSpecificationHash currentHash)
    {
        List<SkillCardFactViewModel> facts =
        [
            new(
                "Teaching episodes",
                BoundedIds(provenance.TeachingEpisodeIds)),
            new("Examples", BoundedIds(provenance.ExampleIds)),
        ];
        foreach (SkillUserAnswerContract answer in provenance.UserAnswers
                     .OrderBy(static item => item.QuestionCode, StringComparer.Ordinal))
        {
            facts.Add(new SkillCardFactViewModel(
                $"Answer: {answer.QuestionCode}",
                answer.SelectedValue));
        }

        int currentEvidence = evidence.Count(item => item.SpecificationHash == currentHash);
        facts.Add(new SkillCardFactViewModel(
            "Current-version evidence",
            $"{currentEvidence.ToString(CultureInfo.InvariantCulture)} record(s)"));
        return facts;
    }

    private static IEnumerable<SkillCardFactViewModel> Compatibility(
        SkillSpecContract specification,
        SkillSpecificationHash hash)
    {
        yield return new SkillCardFactViewModel(
            "Schema",
            specification.SchemaVersion);
        yield return new SkillCardFactViewModel(
            "Compiler",
            $"{specification.Compiler.Kind} {specification.Compiler.Version}");
        yield return new SkillCardFactViewModel(
            "Minimum executor",
            specification.Compatibility.MinimumExecutorVersion);
        yield return new SkillCardFactViewModel("Canonical SkillSpec SHA-256", hash.Value);
    }

    private static SkillCardEvidenceViewModel Evidence(
        SkillCardEvidence evidence,
        SkillSpecificationHash currentHash) =>
        new(
            FormatEvidenceKind(evidence.Kind),
            evidence.ReasonCode,
            evidence.ObservedUtc.ToString("O", CultureInfo.InvariantCulture),
            evidence.SpecificationHash.Value,
            evidence.ArtifactFingerprint?.Value,
            evidence.SpecificationHash == currentHash);

    private static string EvidenceSummary(
        IReadOnlyList<SkillCardEvidence> evidence,
        SkillSpecificationHash currentHash)
    {
        SkillCardEvidence? latest = evidence.FirstOrDefault(item =>
            item.SpecificationHash == currentHash);
        return latest is null
            ? "No current-version dry-run, rehearsal, or verified-run evidence."
            : $"{FormatEvidenceKind(latest.Kind)} — {latest.ReasonCode}";
    }

    private static SkillCardActionViewModel[] BuildActions(
        SkillCardRequest request,
        IReadOnlyList<SkillCardEvidence> evidence,
        SkillSpecificationHash currentHash,
        GrantCapability[] missingCapabilities)
    {
        bool hasCurrentApprovalEvidence = evidence.Any(item =>
            item.SpecificationHash == currentHash &&
            item.Kind is SkillCardEvidenceKind.DryRunPassed or
                SkillCardEvidenceKind.RehearsalPassed);
        bool canApprove = !request.IsDisabled &&
            missingCapabilities.Length == 0 &&
            request.Lifecycle == SkillLifecycleState.Draft &&
            hasCurrentApprovalEvidence;
        string? approvalReason = canApprove
            ? null
            : request.IsDisabled
                ? "The skill is disabled."
                : missingCapabilities.Length > 0
                    ? MissingCapabilityReason(missingCapabilities)
                : request.Lifecycle != SkillLifecycleState.Draft
                    ? "Only a Draft version can be approved."
                    : "This exact version needs a passing dry-run or rehearsal.";

        return
        [
            Action(
                SkillCardActionCode.Rehearse,
                "Rehearse",
                "Rehearse this exact SkillSpec version",
                !request.IsDisabled && missingCapabilities.Length == 0,
                request.IsDisabled
                    ? "The skill is disabled."
                    : missingCapabilities.Length > 0
                        ? MissingCapabilityReason(missingCapabilities)
                        : null),
            Action(
                SkillCardActionCode.Approve,
                "Approve",
                "Approve this exact SkillSpec version",
                canApprove,
                approvalReason),
            Action(
                SkillCardActionCode.Disable,
                "Disable",
                "Disable this skill locally",
                !request.IsDisabled,
                request.IsDisabled ? "The skill is already disabled." : null),
            Action(
                SkillCardActionCode.Correct,
                "Correct",
                "Create a corrected immutable skill version",
                isEnabled: true,
                disabledReason: null),
            Action(
                SkillCardActionCode.Export,
                "Export",
                "Export this skill without authority or credentials",
                isEnabled: true,
                disabledReason: null),
            Action(
                SkillCardActionCode.DeleteLocalHistory,
                "Delete local history",
                "Delete eligible local skill history",
                request.CanDeleteLocalHistory,
                request.CanDeleteLocalHistory
                    ? null
                    : "Retention or recovery policy currently requires this history."),
        ];
    }

    private static GrantCapability[] MissingCapabilities(SkillCardRequest request)
    {
        HashSet<GrantCapability> granted = request.GrantedCapabilities.ToHashSet();
        HashSet<GrantCapability> required =
        [
            GrantCapability.Enumerate,
            GrantCapability.ReadMetadata,
        ];
        foreach (SkillStepContract step in request.Specification.Steps)
        {
            required.Add(step switch
            {
                EnsureDirectoryStepContract => GrantCapability.CreateDirectory,
                RenameFileStepContract => GrantCapability.Rename,
                MoveFileStepContract => GrantCapability.MoveWithinRoot,
                CopyFileStepContract => GrantCapability.CopyWithinRoot,
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            });
        }

        return required
            .Where(capability => !granted.Contains(capability))
            .Order()
            .ToArray();
    }

    private static string GrantedActions(
        IReadOnlyList<GrantCapability> granted,
        GrantCapability[] missing)
    {
        string value = string.Join(", ", granted.Select(FormatCapability));
        return missing.Length == 0
            ? value
            : $"{value}; missing required actions: {string.Join(", ", missing.Select(FormatCapability))}";
    }

    private static string MissingCapabilityReason(
        IEnumerable<GrantCapability> missing) =>
        $"The current grant is missing: {string.Join(", ", missing.Select(FormatCapability))}.";

    private static SkillCardActionViewModel Action(
        SkillCardActionCode code,
        string label,
        string automationName,
        bool isEnabled,
        string? disabledReason) =>
        new(code, label, automationName, isEnabled, disabledReason);

    private static List<SkillCardSemanticChangeViewModel> BuildSemanticDiff(
        SkillSpecContract? parent,
        SkillSpecContract current)
    {
        if (parent is null)
        {
            return [];
        }

        SortedDictionary<string, string> before = SemanticProjection(parent);
        SortedDictionary<string, string> after = SemanticProjection(current);
        SortedSet<string> fields = new(before.Keys, StringComparer.Ordinal);
        fields.UnionWith(after.Keys);
        List<SkillCardSemanticChangeViewModel> changes = [];
        foreach (string field in fields)
        {
            bool hadBefore = before.TryGetValue(field, out string? beforeValue);
            bool hasAfter = after.TryGetValue(field, out string? afterValue);
            if (hadBefore && hasAfter &&
                string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
            {
                continue;
            }

            SkillCardSemanticChangeKind kind = (hadBefore, hasAfter) switch
            {
                (false, true) => SkillCardSemanticChangeKind.Added,
                (true, false) => SkillCardSemanticChangeKind.Removed,
                _ => SkillCardSemanticChangeKind.Changed,
            };
            changes.Add(new SkillCardSemanticChangeViewModel(
                field,
                kind,
                beforeValue,
                afterValue));
        }

        return changes;
    }

    private static SortedDictionary<string, string> SemanticProjection(
        SkillSpecContract specification)
    {
        SkillMatchContract match = specification.Applicability.Match;
        SortedDictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["metadata.name"] = specification.Name,
            ["metadata.description"] = specification.Description,
            ["applicability.rootGrantId"] = specification.Applicability.RootGrantId.ToString("D"),
            ["applicability.invocation"] = specification.Applicability.Invocation.ToString(),
            ["match.origin"] = match.OriginRelativeDirectory ?? "<any>",
            ["match.extensions"] = match.Extensions is null
                ? "<any>"
                : string.Join(
                    ",",
                    match.Extensions
                        .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static value => value, StringComparer.Ordinal)),
            ["match.filename"] = FormatFilenameMatch(match.Filename),
            ["match.maxBytes"] = match.MaxBytes?.ToString(CultureInfo.InvariantCulture) ?? "<none>",
            ["variables"] = string.Join(
                " | ",
                specification.Variables
                    .OrderBy(static variable => variable.Name, StringComparer.Ordinal)
                    .Select(FormatVariable)),
            ["steps"] = string.Join(" | ", BuildOperations(specification.Steps)),
            ["policy"] = FormatPolicy(specification.Policy),
            ["verification"] = FormatVerification(specification.Verification),
            ["compiler"] = $"{specification.Compiler.Kind}:{specification.Compiler.Version}",
            ["compatibility"] =
                $"{specification.Compatibility.ContractVersion}:{specification.Compatibility.MinimumExecutorVersion}",
            ["provenance.answers"] = string.Join(
                " | ",
                specification.Provenance.UserAnswers
                    .OrderBy(static answer => answer.QuestionCode, StringComparer.Ordinal)
                    .Select(static answer => $"{answer.QuestionCode}={answer.SelectedValue}")),
        };
        return values;
    }

    private static string RelativeScope(SkillMatchContract match) =>
        match.OriginRelativeDirectory switch
        {
            null => "Any directory under the granted root",
            "" => "The granted root directory only",
            string directory => $"Exact relative directory: {DisplayPath(directory)}",
        };

    private static string FormatVariableSource(SkillVariableContract variable) =>
        variable.Source switch
        {
            SkillVariableSource.OriginalStem => "original filename stem",
            SkillVariableSource.OriginalExtension => "original extension",
            SkillVariableSource.RegexCapture => $"safe regex capture '{variable.Argument}'",
            SkillVariableSource.FileCreatedYear => "file creation year (UTC)",
            SkillVariableSource.FileCreatedMonth => "file creation month (UTC)",
            SkillVariableSource.FileModifiedYear => "file modified year (UTC)",
            SkillVariableSource.FileModifiedMonth => "file modified month (UTC)",
            SkillVariableSource.UserParameter => $"bounded user parameter '{variable.Argument}'",
            _ => throw new ArgumentOutOfRangeException(nameof(variable)),
        };

    private static string FormatTransforms(IReadOnlyList<SkillVariableTransform> transforms) =>
        transforms.Count == 0
            ? "none"
            : string.Join(
                " → ",
                transforms.Select(static transform => transform switch
                {
                    SkillVariableTransform.Lowercase => "lowercase",
                    SkillVariableTransform.Uppercase => "uppercase",
                    SkillVariableTransform.SlugHyphen => "slug with hyphens",
                    _ => throw new ArgumentOutOfRangeException(nameof(transforms)),
                }));

    private static string FormatVariable(SkillVariableContract variable) =>
        $"{variable.Name}:{variable.Source}:{variable.Argument ?? "<none>"}:{string.Join(",", variable.Transforms)}";

    private static string FormatFilenameMatch(SkillFilenameMatchContract? filename) =>
        filename is null
            ? "<none>"
            : $"prefix={filename.Prefix ?? "<none>"};suffix={filename.Suffix ?? "<none>"};contains={filename.Contains ?? "<none>"};regex={filename.SafeRegex ?? "<none>"};case={filename.CaseSensitive}";

    private static string FormatPolicy(SkillPolicyContract policy) =>
        $"collision={policy.Collision};exactApproval={policy.RequireExactPlanApproval};network={policy.AllowNetworkPaths};reparse={policy.AllowReparsePoints};overwrite={policy.AllowOverwrite};sameVolume={policy.SameVolumeOnly}";

    private static string FormatVerification(SkillVerificationContract verification) =>
        $"allSteps={verification.AllPlannedStepsVerified};unexpected={verification.FailOnUnexpectedChange}";

    private static string FormatCapability(GrantCapability capability) =>
        capability switch
        {
            GrantCapability.Enumerate => "enumerate",
            GrantCapability.ReadMetadata => "read metadata",
            GrantCapability.ReadContentHash => "read bounded content hashes",
            GrantCapability.CreateDirectory => "create directories",
            GrantCapability.Rename => "rename files",
            GrantCapability.MoveWithinRoot => "move files within root",
            GrantCapability.CopyWithinRoot => "copy files within root",
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    private static string FormatEvidenceKind(SkillCardEvidenceKind kind) =>
        kind switch
        {
            SkillCardEvidenceKind.TeachingComplete => "Teaching complete",
            SkillCardEvidenceKind.NeedsClarification => "Clarification required",
            SkillCardEvidenceKind.NeedsMoreExamples => "More examples required",
            SkillCardEvidenceKind.DryRunPassed => "Dry-run passed",
            SkillCardEvidenceKind.RehearsalPassed => "Rehearsal passed",
            SkillCardEvidenceKind.RehearsalFailed => "Rehearsal failed",
            SkillCardEvidenceKind.VerifiedRun => "Verified run",
            SkillCardEvidenceKind.VerificationFailed => "Verification failed",
            SkillCardEvidenceKind.Stale => "Evidence stale",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string BoundedIds(IReadOnlyList<Guid> ids)
    {
        const int maximumShown = 5;
        string shown = string.Join(
            ", ",
            ids.Order().Take(maximumShown).Select(static id => id.ToString("D")));
        int remaining = ids.Count - Math.Min(ids.Count, maximumShown);
        return remaining == 0
            ? $"{ids.Count.ToString(CultureInfo.InvariantCulture)}: {shown}"
            : $"{ids.Count.ToString(CultureInfo.InvariantCulture)}: {shown}, … {remaining.ToString(CultureInfo.InvariantCulture)} more";
    }

    private static void AddOptional(
        List<SkillCardFactViewModel> facts,
        string label,
        string? value)
    {
        if (value is not null)
        {
            facts.Add(new SkillCardFactViewModel(label, value));
        }
    }

    private static string DisplayPath(string value) => value.Replace('/', '\\');
}

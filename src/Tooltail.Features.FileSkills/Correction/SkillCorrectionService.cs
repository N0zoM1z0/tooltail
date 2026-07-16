using System.Collections.ObjectModel;
using System.Text;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Compilation;
using Tooltail.Features.FileSkills.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Correction;

public enum SkillCorrectionKind
{
    PositiveExample,
    NegativeExample,
    ExplicitClarification,
}

public sealed record SkillSemanticDiff(
    bool MatchChanged,
    bool TransformationChanged,
    bool PolicyChanged,
    bool VerificationChanged,
    bool ScopeBindingChanged,
    IReadOnlyList<string> ChangedFields)
{
    public bool HasExecutableChange =>
        MatchChanged ||
        TransformationChanged ||
        PolicyChanged ||
        VerificationChanged ||
        ScopeBindingChanged;
}

public sealed record SkillCorrectionRequest
{
    public SkillCorrectionRequest(
        SkillCorrectionKind kind,
        SkillSpecContract parentSpecification,
        DateTimeOffset createdUtc,
        IEnumerable<TeachingFileExample> positiveExamples,
        IEnumerable<FolderSnapshotEntry>? negativeExamples = null,
        IEnumerable<SkillUserAnswerContract>? clarifications = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(parentSpecification);
        if (createdUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Correction time must use UTC.", nameof(createdUtc));
        }

        ArgumentNullException.ThrowIfNull(positiveExamples);
        TeachingFileExample[] positives = positiveExamples.ToArray();
        FolderSnapshotEntry[] negatives = (negativeExamples ?? []).ToArray();
        SkillUserAnswerContract[] answers = (clarifications ?? []).ToArray();
        if (positives.Any(static value => value is null) ||
            negatives.Any(static value => value is null) ||
            answers.Any(static value => value is null))
        {
            throw new ArgumentException("Correction evidence cannot contain null values.");
        }

        Kind = kind;
        ParentSpecification = parentSpecification;
        CreatedUtc = createdUtc;
        PositiveExamples = new ReadOnlyCollection<TeachingFileExample>(positives);
        NegativeExamples = new ReadOnlyCollection<FolderSnapshotEntry>(negatives);
        Clarifications = new ReadOnlyCollection<SkillUserAnswerContract>(answers);
    }

    public SkillCorrectionKind Kind { get; }

    public SkillSpecContract ParentSpecification { get; }

    public DateTimeOffset CreatedUtc { get; }

    public IReadOnlyList<TeachingFileExample> PositiveExamples { get; }

    public IReadOnlyList<FolderSnapshotEntry> NegativeExamples { get; }

    public IReadOnlyList<SkillUserAnswerContract> Clarifications { get; }
}

public enum SkillCorrectionStatus
{
    Ready,
    NeedsClarification,
    NeedsMoreExamples,
    Invalid,
    Unsupported,
    NoCandidate,
    NoExecutableChange,
}

public sealed record SkillCorrectionResult(
    SkillCorrectionStatus Status,
    string ReasonCode,
    SkillCompilationResult? Compilation,
    SkillSpecContract? Specification,
    SkillSpecificationHash? SpecificationHash,
    SkillSemanticDiff? SemanticDiff,
    string? SemanticDiffJson)
{
    public bool IsReady => Status == SkillCorrectionStatus.Ready;

    public SkillLifecycleState? ProposedLifecycle => IsReady
        ? SkillLifecycleState.Draft
        : null;

    public bool RequiresRehearsal => IsReady;

    public bool RequiresExactPlanApproval => IsReady;
}

public static class SkillCorrectionService
{
    public static SkillCorrectionResult Compile(SkillCorrectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SkillValidationResult parentValidation =
            SkillSpecValidator.Validate(request.ParentSpecification);
        string? invalidReason = ValidateRequest(request, parentValidation);
        if (invalidReason is not null)
        {
            return Failure(SkillCorrectionStatus.Invalid, invalidReason);
        }

        SkillSpecContract parent = request.ParentSpecification;
        SkillCompilationResult compilation = DeterministicSkillCompiler.Compile(
            new SkillCompilationRequest(
                new SkillId(parent.SkillId),
                checked(parent.Version + 1),
                parent.Name,
                parent.Description,
                new GrantId(parent.Applicability.RootGrantId),
                request.CreatedUtc,
                request.PositiveExamples,
                request.NegativeExamples,
                request.Clarifications,
                parent.Version));
        if (compilation.Status != SkillCompilationStatus.Ready)
        {
            return new SkillCorrectionResult(
                MapStatus(compilation.Status),
                compilation.ReasonCode,
                compilation,
                null,
                null,
                null,
                null);
        }

        SkillSpecContract corrected = compilation.SelectedCandidate!.Specification;
        SkillSemanticDiff diff = Compare(parent, corrected);
        if (!diff.HasExecutableChange)
        {
            return new SkillCorrectionResult(
                SkillCorrectionStatus.NoExecutableChange,
                "correction.no_executable_change",
                compilation,
                null,
                null,
                diff,
                EncodeDiff(diff));
        }

        return new SkillCorrectionResult(
            SkillCorrectionStatus.Ready,
            "correction.ready",
            compilation,
            corrected,
            CanonicalSkillSpec.ComputeHash(corrected),
            diff,
            EncodeDiff(diff));
    }

    public static SkillSemanticDiff Compare(
        SkillSpecContract parent,
        SkillSpecContract corrected)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(corrected);
        if (!SkillSpecValidator.Validate(parent).IsValid ||
            !SkillSpecValidator.Validate(corrected).IsValid ||
            parent.SkillId != corrected.SkillId ||
            corrected.Version <= parent.Version ||
            corrected.Provenance.ParentVersion != parent.Version)
        {
            throw new ArgumentException(
                "A semantic correction requires valid parent-linked versions of one skill.");
        }

        bool match = !Equivalent(
            parent.Applicability.Match,
            corrected.Applicability.Match);
        bool transform = !Equivalent(parent.Variables, corrected.Variables) ||
            !Equivalent(parent.Steps, corrected.Steps);
        bool policy = !Equivalent(parent.Policy, corrected.Policy);
        bool verification = !Equivalent(parent.Verification, corrected.Verification);
        bool scope = parent.Applicability.RootGrantId !=
            corrected.Applicability.RootGrantId;
        List<string> fields = [];
        AddIf(fields, match, "match");
        AddIf(fields, transform, "transformation");
        AddIf(fields, policy, "policy");
        AddIf(fields, verification, "verification");
        AddIf(fields, scope, "scope_binding");
        return new SkillSemanticDiff(
            match,
            transform,
            policy,
            verification,
            scope,
            new ReadOnlyCollection<string>(fields));
    }

    private static string? ValidateRequest(
        SkillCorrectionRequest request,
        SkillValidationResult parentValidation)
    {
        if (!parentValidation.IsValid)
        {
            return "correction.parent_invalid";
        }

        SkillSpecContract parent = request.ParentSpecification;
        if (parent.Version == int.MaxValue || request.CreatedUtc <= parent.CreatedAt)
        {
            return "correction.version_time_invalid";
        }

        if (request.PositiveExamples.Count is < 2 or > 5 ||
            request.NegativeExamples.Count > 32 ||
            request.Clarifications.Count > 2)
        {
            return "correction.evidence_bounds_invalid";
        }

        HashSet<Guid> suppliedExamples = request.PositiveExamples
            .Select(static example => example.Id.Value)
            .ToHashSet();
        if (!parent.Provenance.ExampleIds.All(suppliedExamples.Contains) ||
            request.PositiveExamples.Any(example =>
                example.GrantId.Value != parent.Applicability.RootGrantId))
        {
            return "correction.evidence_lineage_invalid";
        }

        int newPositiveCount = suppliedExamples.Count -
            parent.Provenance.ExampleIds.Distinct().Count();
        bool clarificationChanged = !AnswersEqual(
            parent.Provenance.UserAnswers,
            request.Clarifications);
        return request.Kind switch
        {
            SkillCorrectionKind.PositiveExample when newPositiveCount < 1 =>
                "correction.positive_example_missing",
            SkillCorrectionKind.NegativeExample when request.NegativeExamples.Count < 1 =>
                "correction.negative_example_missing",
            SkillCorrectionKind.ExplicitClarification when !clarificationChanged =>
                "correction.clarification_unchanged",
            _ => null,
        };
    }

    private static bool AnswersEqual(
        IReadOnlyList<SkillUserAnswerContract> left,
        IReadOnlyList<SkillUserAnswerContract> right) =>
        left.OrderBy(static answer => answer.QuestionCode, StringComparer.Ordinal)
            .Select(static answer => (answer.QuestionCode, answer.SelectedValue))
            .SequenceEqual(
                right.OrderBy(static answer => answer.QuestionCode, StringComparer.Ordinal)
                    .Select(static answer => (answer.QuestionCode, answer.SelectedValue)));

    private static SkillCorrectionStatus MapStatus(SkillCompilationStatus status) =>
        status switch
        {
            SkillCompilationStatus.NeedsClarification =>
                SkillCorrectionStatus.NeedsClarification,
            SkillCompilationStatus.NeedsMoreExamples =>
                SkillCorrectionStatus.NeedsMoreExamples,
            SkillCompilationStatus.InvalidRequest => SkillCorrectionStatus.Invalid,
            SkillCompilationStatus.UnsupportedEvidence => SkillCorrectionStatus.Unsupported,
            SkillCompilationStatus.NoCandidate => SkillCorrectionStatus.NoCandidate,
            SkillCompilationStatus.Ready => SkillCorrectionStatus.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static SkillCorrectionResult Failure(
        SkillCorrectionStatus status,
        string reasonCode) =>
        new(status, reasonCode, null, null, null, null, null);

    private static bool Equivalent<T>(T left, T right) =>
        ContractJson.Serialize(left).AsSpan().SequenceEqual(ContractJson.Serialize(right));

    private static string EncodeDiff(SkillSemanticDiff diff) =>
        Encoding.UTF8.GetString(ContractJson.Serialize(diff));

    private static void AddIf(List<string> fields, bool condition, string field)
    {
        if (condition)
        {
            fields.Add(field);
        }
    }
}

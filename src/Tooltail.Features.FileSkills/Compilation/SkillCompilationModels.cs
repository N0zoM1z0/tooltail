using System.Collections.ObjectModel;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Compilation;

public sealed record TeachingFileExample
{
    public TeachingFileExample(
        ExampleId id,
        TeachingEpisodeId episodeId,
        GrantId grantId,
        ResourceRootIdentity rootIdentity,
        ReconciledFileEffect effect)
    {
        if (id.Value == Guid.Empty ||
            episodeId.Value == Guid.Empty ||
            grantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Teaching example identifiers cannot be empty.");
        }

        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(effect);
        if (!effect.IsSupportedForCompilation ||
            effect.Kind is not (ReconciledEffectKind.Renamed or
                ReconciledEffectKind.Moved or
                ReconciledEffectKind.Copied) ||
            effect.Before is not { Kind: SnapshotEntryKind.File } ||
            effect.After is not { Kind: SnapshotEntryKind.File })
        {
            throw new ArgumentException(
                "A compiler example must be one supported normalized file effect.",
                nameof(effect));
        }

        Id = id;
        EpisodeId = episodeId;
        GrantId = grantId;
        RootIdentity = rootIdentity;
        Effect = effect;
    }

    public ExampleId Id { get; }

    public TeachingEpisodeId EpisodeId { get; }

    public GrantId GrantId { get; }

    public ResourceRootIdentity RootIdentity { get; }

    public ReconciledFileEffect Effect { get; }

    public FolderSnapshotEntry Source => Effect.Before!;

    public FolderSnapshotEntry Destination => Effect.After!;
}

public sealed record SkillCompilationRequest
{
    public SkillCompilationRequest(
        SkillId skillId,
        int version,
        string name,
        string description,
        GrantId rootGrantId,
        DateTimeOffset createdUtc,
        IEnumerable<TeachingFileExample> examples,
        IEnumerable<FolderSnapshotEntry>? exclusions = null,
        IEnumerable<SkillUserAnswerContract>? userAnswers = null,
        int? parentVersion = null)
    {
        if (skillId.Value == Guid.Empty || rootGrantId.Value == Guid.Empty)
        {
            throw new ArgumentException("Compilation authority identifiers cannot be empty.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (createdUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Compilation time must use UTC.", nameof(createdUtc));
        }

        ArgumentNullException.ThrowIfNull(examples);
        TeachingFileExample[] materializedExamples = examples.ToArray();
        FolderSnapshotEntry[] materializedExclusions = (exclusions ?? []).ToArray();
        SkillUserAnswerContract[] materializedAnswers = (userAnswers ?? []).ToArray();
        if (materializedExamples.Any(static example => example is null) ||
            materializedExclusions.Any(static exclusion => exclusion is null) ||
            materializedAnswers.Any(static answer => answer is null))
        {
            throw new ArgumentException("Compiler inputs cannot contain null values.");
        }

        if (parentVersion is < 1 || parentVersion >= version)
        {
            throw new ArgumentOutOfRangeException(nameof(parentVersion));
        }

        SkillId = skillId;
        Version = version;
        Name = name;
        Description = description;
        RootGrantId = rootGrantId;
        CreatedUtc = createdUtc;
        Examples = new ReadOnlyCollection<TeachingFileExample>(materializedExamples);
        Exclusions = new ReadOnlyCollection<FolderSnapshotEntry>(materializedExclusions);
        UserAnswers = new ReadOnlyCollection<SkillUserAnswerContract>(materializedAnswers);
        ParentVersion = parentVersion;
    }

    public SkillId SkillId { get; }

    public int Version { get; }

    public string Name { get; }

    public string Description { get; }

    public GrantId RootGrantId { get; }

    public DateTimeOffset CreatedUtc { get; }

    public IReadOnlyList<TeachingFileExample> Examples { get; }

    public IReadOnlyList<FolderSnapshotEntry> Exclusions { get; }

    public IReadOnlyList<SkillUserAnswerContract> UserAnswers { get; }

    public int? ParentVersion { get; }
}

public enum SkillCompilationStatus
{
    Ready,
    NeedsClarification,
    NeedsMoreExamples,
    InvalidRequest,
    UnsupportedEvidence,
    NoCandidate,
}

public sealed record SkillCandidateScore(
    int ExactCoverage,
    int AssumptionCount,
    int ExplanationLength,
    int StableSemanticRank,
    int CollisionRiskRank);

public sealed record SkillCandidate(
    string Key,
    SkillSpecContract Specification,
    SkillCandidateScore Score,
    string Summary);

public sealed record CompilerQuestionOption(string Value, string Label);

public sealed record CompilerQuestion
{
    public CompilerQuestion(
        string code,
        string field,
        string prompt,
        IEnumerable<CompilerQuestionOption> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(options);
        CompilerQuestionOption[] materialized = options.ToArray();
        if (materialized.Length is < 2 or > 4 ||
            materialized.Any(static option =>
                option is null ||
                string.IsNullOrWhiteSpace(option.Value) ||
                string.IsNullOrWhiteSpace(option.Label)))
        {
            throw new ArgumentException("A compiler question needs two to four bounded options.", nameof(options));
        }

        Code = code;
        Field = field;
        Prompt = prompt;
        Options = new ReadOnlyCollection<CompilerQuestionOption>(materialized);
    }

    public string Code { get; }

    public string Field { get; }

    public string Prompt { get; }

    public IReadOnlyList<CompilerQuestionOption> Options { get; }
}

public sealed record CompilerRejectedCause(string CandidateKey, string Code);

public sealed record SkillCompilationResult
{
    internal SkillCompilationResult(
        SkillCompilationStatus status,
        string reasonCode,
        IEnumerable<SkillCandidate> candidates,
        IEnumerable<CompilerQuestion> questions,
        IEnumerable<CompilerRejectedCause> rejectedCauses)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        SkillCandidate[] materializedCandidates = candidates.ToArray();
        CompilerQuestion[] materializedQuestions = questions.ToArray();
        CompilerRejectedCause[] materializedRejected = rejectedCauses.ToArray();
        if (materializedQuestions.Length > 2)
        {
            throw new ArgumentException("The compiler cannot ask more than two questions.");
        }

        Status = status;
        ReasonCode = reasonCode;
        Candidates = new ReadOnlyCollection<SkillCandidate>(materializedCandidates);
        Questions = new ReadOnlyCollection<CompilerQuestion>(materializedQuestions);
        RejectedCauses = new ReadOnlyCollection<CompilerRejectedCause>(materializedRejected);
    }

    public SkillCompilationStatus Status { get; }

    public string ReasonCode { get; }

    public IReadOnlyList<SkillCandidate> Candidates { get; }

    public IReadOnlyList<CompilerQuestion> Questions { get; }

    public IReadOnlyList<CompilerRejectedCause> RejectedCauses { get; }

    public SkillCandidate? SelectedCandidate =>
        Status == SkillCompilationStatus.Ready && Candidates.Count == 1
            ? Candidates[0]
            : null;
}

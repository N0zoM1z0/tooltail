using System.Collections.ObjectModel;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Planning;

public enum SkillPlanningStatus
{
    Ready,
    InvalidSkill,
    InvalidRequest,
    AuthorityDenied,
    IncompleteSnapshot,
    NoMatches,
    Conflict,
    LimitExceeded,
}

public sealed record SkillPlanningDiagnostic
{
    public SkillPlanningDiagnostic(
        string code,
        string field,
        string message,
        string? sourceRelativePath = null,
        string? destinationRelativePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Field = field;
        Message = message;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
    }

    public string Code { get; }

    public string Field { get; }

    public string Message { get; }

    public string? SourceRelativePath { get; }

    public string? DestinationRelativePath { get; }
}

public sealed record SkillPlanningLimits
{
    public SkillPlanningLimits(
        int maximumMatches,
        int maximumOperations,
        int maximumUserParameters,
        TimeSpan maximumSnapshotAge,
        TimeSpan maximumPlanLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMatches, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOperations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUserParameters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximumSnapshotAge,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximumPlanLifetime,
            TimeSpan.Zero);

        MaximumMatches = maximumMatches;
        MaximumOperations = maximumOperations;
        MaximumUserParameters = maximumUserParameters;
        MaximumSnapshotAge = maximumSnapshotAge;
        MaximumPlanLifetime = maximumPlanLifetime;
    }

    public int MaximumMatches { get; }

    public int MaximumOperations { get; }

    public int MaximumUserParameters { get; }

    public TimeSpan MaximumSnapshotAge { get; }

    public TimeSpan MaximumPlanLifetime { get; }

    public static SkillPlanningLimits Default { get; } = new(
        maximumMatches: 1_000,
        maximumOperations: 4_096,
        maximumUserParameters: 16,
        maximumSnapshotAge: TimeSpan.FromMinutes(5),
        maximumPlanLifetime: TimeSpan.FromMinutes(30));
}

public sealed record SkillPlanningRequest
{
    public SkillPlanningRequest(
        PlanId planId,
        SkillSpecContract specification,
        SkillSpecificationHash specificationHash,
        LocalFolderGrant grant,
        FolderSnapshot snapshot,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc,
        IReadOnlyDictionary<string, string>? userParameters = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(specificationHash);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(snapshot);

        PlanId = planId;
        Specification = specification;
        SpecificationHash = specificationHash;
        Grant = grant;
        Snapshot = snapshot;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
        UserParameters = userParameters;
    }

    public PlanId PlanId { get; }

    public SkillSpecContract Specification { get; }

    public SkillSpecificationHash SpecificationHash { get; }

    public LocalFolderGrant Grant { get; }

    public FolderSnapshot Snapshot { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public IReadOnlyDictionary<string, string>? UserParameters { get; }
}

public sealed record SkillPlanningResult
{
    internal SkillPlanningResult(
        SkillPlanningStatus status,
        ExecutionPlan? plan,
        int matchedFileCount,
        IEnumerable<SkillPlanningDiagnostic> diagnostics)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matchedFileCount);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!Enum.IsDefined(status) ||
            (status == SkillPlanningStatus.Ready) != (plan is not null))
        {
            throw new ArgumentException("Planning status and exact plan must agree.");
        }

        SkillPlanningDiagnostic[] orderedDiagnostics = diagnostics
            .OrderBy(static diagnostic => diagnostic.SourceRelativePath, PathComparer.Instance)
            .ThenBy(static diagnostic => diagnostic.DestinationRelativePath, PathComparer.Instance)
            .ThenBy(static diagnostic => diagnostic.Field, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray();
        if ((status == SkillPlanningStatus.Ready) != (orderedDiagnostics.Length == 0))
        {
            throw new ArgumentException("Successful plans have no diagnostics; failures require one.");
        }

        Status = status;
        Plan = plan;
        MatchedFileCount = matchedFileCount;
        Diagnostics = new ReadOnlyCollection<SkillPlanningDiagnostic>(orderedDiagnostics);
    }

    public SkillPlanningStatus Status { get; }

    public ExecutionPlan? Plan { get; }

    public int MatchedFileCount { get; }

    public IReadOnlyList<SkillPlanningDiagnostic> Diagnostics { get; }

    private sealed class PathComparer : IComparer<string?>
    {
        public static PathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            int insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
        }
    }
}

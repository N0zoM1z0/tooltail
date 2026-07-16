using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;

namespace Tooltail.Application.Diagnostics;

public sealed record DiagnosticResearchSnapshot(
    bool IsEnabled,
    int EventCount,
    long EventBytes,
    string ReasonCode);

public sealed record DiagnosticContentPolicy(
    bool ContainsRawPaths,
    bool ContainsFileNames,
    bool ContainsFileContents,
    bool ContainsWindowTitles,
    bool ContainsModelText,
    bool ContainsCredentials,
    bool ContainsUserOrMachineIdentity);

public sealed record DiagnosticBodySnapshot(
    CompanionBodyState State,
    NormalizedAgentToolKind? ToolKind,
    int ParallelUnitCount,
    string ReasonCode);

public sealed record DiagnosticGrantCounts(
    int Active,
    int Expired,
    int Revoked);

public sealed record DiagnosticSkillCounts(
    int Draft,
    int Approved,
    int Practiced,
    int Reliable,
    int Delegated,
    int Stale);

public sealed record DiagnosticLessonCounts(
    int Pending,
    int Complete,
    int Incomplete,
    int Ambiguous,
    int Unsupported);

public sealed record DiagnosticExecutionCounts(
    int Running,
    int Verified,
    int Failed,
    int RecoveryRequired,
    int Cancelled,
    int WithReceipt);

public sealed record DiagnosticResearchCounts(
    bool IsEnabled,
    int EventCount,
    long EventBytes,
    string ReasonCode);

public sealed record DiagnosticExportDocument(
    string ContractVersion,
    DateTimeOffset GeneratedUtc,
    string ProductVersion,
    bool CompanionPresent,
    DiagnosticContentPolicy ContentPolicy,
    DiagnosticBodySnapshot Body,
    DiagnosticGrantCounts Grants,
    DiagnosticSkillCounts Skills,
    DiagnosticLessonCounts Lessons,
    DiagnosticExecutionCounts Executions,
    int RecoveryCandidateCount,
    DiagnosticResearchCounts Research);

public sealed record DiagnosticEncodingResult(
    bool IsSuccess,
    string ReasonCode,
    byte[]? Bytes,
    DiagnosticExportDocument? Document);

public static partial class DiagnosticExportBuilder
{
    public const int MaximumBytes = 64 * 1024;
    private const string ContractVersion = "tooltail.diagnostic-export/1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false),
        },
        MaxDepth = 16,
    };

    public static DiagnosticEncodingResult Build(
        FileSkillWorkspaceStateRecord workspace,
        ExecutionRecoveryScanResult recovery,
        CompanionBodyProjection body,
        DiagnosticResearchSnapshot research,
        DateTimeOffset generatedUtc,
        string productVersion)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(research);
        if (!recovery.IsSuccess || generatedUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(productVersion) || productVersion.Length > 64 ||
            !IsReasonCode(body.ReasonCode) ||
            !IsReasonCode(research.ReasonCode) ||
            research.EventCount < 0 || research.EventBytes < 0)
        {
            return Failure("diagnostic.input_invalid");
        }

        DiagnosticExportDocument document = new(
            ContractVersion,
            generatedUtc,
            productVersion,
            CompanionPresent: true,
            new DiagnosticContentPolicy(
                ContainsRawPaths: false,
                ContainsFileNames: false,
                ContainsFileContents: false,
                ContainsWindowTitles: false,
                ContainsModelText: false,
                ContainsCredentials: false,
                ContainsUserOrMachineIdentity: false),
            new DiagnosticBodySnapshot(
                body.State,
                body.ToolKind,
                body.ParallelUnitCount,
                body.ReasonCode),
            CountGrants(workspace.Grants, generatedUtc),
            CountSkills(workspace.CurrentSkills),
            CountLessons(workspace.TeachingEpisodes),
            CountExecutions(workspace.Executions),
            recovery.Candidates.Count,
            new DiagnosticResearchCounts(
                research.IsEnabled,
                research.EventCount,
                research.EventBytes,
                research.ReasonCode));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is < 2 or > MaximumBytes)
        {
            return Failure("diagnostic.output_oversized");
        }

        DiagnosticEncodingResult readback = Parse(bytes);
        return readback.IsSuccess && readback.Document == document
            ? new DiagnosticEncodingResult(
                true,
                "diagnostic.preview_ready",
                bytes,
                document)
            : Failure(readback.ReasonCode);
    }

    public static DiagnosticEncodingResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is < 2 or > MaximumBytes)
        {
            return Failure("diagnostic.input_size_invalid");
        }

        try
        {
            DiagnosticExportDocument? document =
                JsonSerializer.Deserialize<DiagnosticExportDocument>(
                    utf8Json,
                    JsonOptions);
            return IsValid(document)
                ? new DiagnosticEncodingResult(
                    true,
                    "diagnostic.valid",
                    utf8Json.ToArray(),
                    document)
                : Failure("diagnostic.document_invalid");
        }
        catch (JsonException)
        {
            return Failure("diagnostic.json_invalid");
        }
    }

    private static DiagnosticGrantCounts CountGrants(
        IReadOnlyList<LocalFolderGrantStateRecord> grants,
        DateTimeOffset now) =>
        new(
            grants.Count(grant =>
                grant.Grant.State == ResourceGrantState.Active &&
                (grant.Grant.ExpiresAt is null || grant.Grant.ExpiresAt > now)),
            grants.Count(grant =>
                grant.Grant.State == ResourceGrantState.Active &&
                grant.Grant.ExpiresAt is not null &&
                grant.Grant.ExpiresAt <= now),
            grants.Count(grant => grant.Grant.State == ResourceGrantState.Revoked));

    private static DiagnosticSkillCounts CountSkills(
        IReadOnlyList<SkillVersionStateRecord> skills) =>
        new(
            skills.Count(skill => skill.Version.Lifecycle == SkillLifecycleState.Draft),
            skills.Count(skill => skill.Version.Lifecycle == SkillLifecycleState.Approved),
            skills.Count(skill => skill.Version.Lifecycle == SkillLifecycleState.Practiced),
            skills.Count(skill => skill.Version.Lifecycle == SkillLifecycleState.Reliable),
            skills.Count(skill => skill.Version.Lifecycle == SkillLifecycleState.Delegated),
            skills.Count(skill => skill.Version.Lifecycle == SkillLifecycleState.Stale));

    private static DiagnosticLessonCounts CountLessons(
        IReadOnlyList<TeachingEpisodeSummaryStateRecord> lessons) =>
        new(
            lessons.Count(lesson =>
                lesson.EvidenceStatus == PersistedTeachingEvidenceStatus.Pending),
            lessons.Count(lesson =>
                lesson.EvidenceStatus == PersistedTeachingEvidenceStatus.Complete),
            lessons.Count(lesson =>
                lesson.EvidenceStatus == PersistedTeachingEvidenceStatus.Incomplete),
            lessons.Count(lesson =>
                lesson.EvidenceStatus == PersistedTeachingEvidenceStatus.Ambiguous),
            lessons.Count(lesson =>
                lesson.EvidenceStatus == PersistedTeachingEvidenceStatus.Unsupported));

    private static DiagnosticExecutionCounts CountExecutions(
        IReadOnlyList<ExecutionSummaryStateRecord> executions) =>
        new(
            executions.Count(execution =>
                execution.Status == PersistedExecutionStatus.Running),
            executions.Count(execution =>
                execution.Status == PersistedExecutionStatus.Verified),
            executions.Count(execution =>
                execution.Status == PersistedExecutionStatus.Failed),
            executions.Count(execution =>
                execution.Status == PersistedExecutionStatus.RecoveryRequired),
            executions.Count(execution =>
                execution.Status == PersistedExecutionStatus.Cancelled),
            executions.Count(static execution => execution.HasReceipt));

    private static bool IsValid(DiagnosticExportDocument? document) =>
        document is not null &&
        document.ContractVersion == ContractVersion &&
        document.GeneratedUtc.Offset == TimeSpan.Zero &&
        !string.IsNullOrWhiteSpace(document.ProductVersion) &&
        document.ProductVersion.Length <= 64 &&
        document.CompanionPresent &&
        document.ContentPolicy is not null &&
        document.Body is not null &&
        document.Grants is not null &&
        document.Skills is not null &&
        document.Lessons is not null &&
        document.Executions is not null &&
        document.Research is not null &&
        !document.ContentPolicy.ContainsRawPaths &&
        !document.ContentPolicy.ContainsFileNames &&
        !document.ContentPolicy.ContainsFileContents &&
        !document.ContentPolicy.ContainsWindowTitles &&
        !document.ContentPolicy.ContainsModelText &&
        !document.ContentPolicy.ContainsCredentials &&
        !document.ContentPolicy.ContainsUserOrMachineIdentity &&
        Enum.IsDefined(document.Body.State) &&
        (document.Body.ToolKind is null || Enum.IsDefined(document.Body.ToolKind.Value)) &&
        document.Body.ParallelUnitCount is >= 0 and <= 1_000 &&
        IsReasonCode(document.Body.ReasonCode) &&
        IsReasonCode(document.Research.ReasonCode) &&
        CountsAreValid(document);

    private static bool CountsAreValid(DiagnosticExportDocument document)
    {
        int[] counts =
        [
            document.Grants.Active,
            document.Grants.Expired,
            document.Grants.Revoked,
            document.Skills.Draft,
            document.Skills.Approved,
            document.Skills.Practiced,
            document.Skills.Reliable,
            document.Skills.Delegated,
            document.Skills.Stale,
            document.Lessons.Pending,
            document.Lessons.Complete,
            document.Lessons.Incomplete,
            document.Lessons.Ambiguous,
            document.Lessons.Unsupported,
            document.Executions.Running,
            document.Executions.Verified,
            document.Executions.Failed,
            document.Executions.RecoveryRequired,
            document.Executions.Cancelled,
            document.Executions.WithReceipt,
            document.RecoveryCandidateCount,
            document.Research.EventCount,
        ];
        return counts.All(static count => count is >= 0 and <= 100_000) &&
            document.Research.EventBytes is >= 0 and <= 8 * 1024 * 1024;
    }

    private static bool IsReasonCode(string? value) =>
        value is not null && ReasonCodePattern().IsMatch(value);

    private static DiagnosticEncodingResult Failure(string reasonCode) =>
        new(false, reasonCode, null, null);

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodePattern();
}

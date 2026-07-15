using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tooltail.Contracts.Skills;
using Tooltail.Domain.Execution;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Presentation;

public enum SkillCardEvidenceKind
{
    TeachingComplete,
    NeedsClarification,
    NeedsMoreExamples,
    DryRunPassed,
    RehearsalPassed,
    RehearsalFailed,
    VerifiedRun,
    VerificationFailed,
    Stale,
}

public enum SkillCardActionCode
{
    Rehearse,
    Approve,
    Disable,
    Correct,
    Export,
    DeleteLocalHistory,
}

public enum SkillCardSemanticChangeKind
{
    Added,
    Removed,
    Changed,
}

public sealed record SkillCardEvidence
{
    public SkillCardEvidence(
        SkillCardEvidenceKind kind,
        string reasonCode,
        DateTimeOffset observedUtc,
        SkillSpecificationHash specificationHash,
        PlanFingerprint? artifactFingerprint = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (reasonCode.Length > 100 ||
            reasonCode.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "Skill Card evidence requires a bounded stable reason code.",
                nameof(reasonCode));
        }

        if (observedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Skill Card evidence timestamps must use UTC.",
                nameof(observedUtc));
        }

        ArgumentNullException.ThrowIfNull(specificationHash);
        bool needsArtifact = kind is
            SkillCardEvidenceKind.DryRunPassed or
            SkillCardEvidenceKind.RehearsalPassed or
            SkillCardEvidenceKind.RehearsalFailed or
            SkillCardEvidenceKind.VerifiedRun or
            SkillCardEvidenceKind.VerificationFailed;
        if (needsArtifact != (artifactFingerprint is not null))
        {
            throw new ArgumentException(
                "Plan-derived evidence and exact artifact fingerprints must agree.",
                nameof(artifactFingerprint));
        }

        Kind = kind;
        ReasonCode = reasonCode;
        ObservedUtc = observedUtc;
        SpecificationHash = specificationHash;
        ArtifactFingerprint = artifactFingerprint;
    }

    public SkillCardEvidenceKind Kind { get; }

    public string ReasonCode { get; }

    public DateTimeOffset ObservedUtc { get; }

    public SkillSpecificationHash SpecificationHash { get; }

    public PlanFingerprint? ArtifactFingerprint { get; }
}

public sealed record SkillCardSample
{
    public SkillCardSample(string sourceRelativePath, string destinationRelativePath)
    {
        PathSafetyResult<WindowsRelativePath> source =
            WindowsPathPolicy.ParseRelative(sourceRelativePath);
        PathSafetyResult<WindowsRelativePath> destination =
            WindowsPathPolicy.ParseRelative(destinationRelativePath);
        if (!source.IsSuccess ||
            !destination.IsSuccess ||
            !WindowsPathPolicy.ValidateDistinctPair(
                source.Value!,
                destination.Value!).IsSuccess)
        {
            throw new ArgumentException(
                "Skill Card samples require one safe distinct relative path pair.");
        }

        SourceRelativePath = source.Value!.Value;
        DestinationRelativePath = destination.Value!.Value;
    }

    public string SourceRelativePath { get; }

    public string DestinationRelativePath { get; }
}

public sealed record SkillCardRequest
{
    public SkillCardRequest(
        SkillSpecContract specification,
        SkillLifecycleState lifecycle,
        string grantedRootLabel,
        IEnumerable<GrantCapability> grantedCapabilities,
        IEnumerable<SkillCardSample>? samples = null,
        IEnumerable<SkillCardEvidence>? evidence = null,
        SkillSpecContract? parentSpecification = null,
        bool isDisabled = false,
        bool canDeleteLocalHistory = false)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!Enum.IsDefined(lifecycle))
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(grantedRootLabel);
        if (grantedRootLabel.Length > 240 || grantedRootLabel.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The granted root label must be a bounded display string.",
                nameof(grantedRootLabel));
        }

        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        GrantCapability[] materializedCapabilities = grantedCapabilities
            .Distinct()
            .Order()
            .ToArray();
        SkillCardSample[] materializedSamples = (samples ?? []).ToArray();
        SkillCardEvidence[] materializedEvidence = (evidence ?? []).ToArray();
        if (materializedCapabilities.Length == 0 ||
            materializedCapabilities.Any(static capability => !Enum.IsDefined(capability)) ||
            materializedSamples.Length > 5 ||
            materializedSamples.Any(static sample => sample is null) ||
            materializedEvidence.Length > 32 ||
            materializedEvidence.Any(static item => item is null))
        {
            throw new ArgumentException("Skill Card inputs exceed the closed bounded model.");
        }

        Specification = specification;
        Lifecycle = lifecycle;
        GrantedRootLabel = grantedRootLabel;
        GrantedCapabilities = new ReadOnlyCollection<GrantCapability>(
            materializedCapabilities);
        Samples = new ReadOnlyCollection<SkillCardSample>(materializedSamples);
        Evidence = new ReadOnlyCollection<SkillCardEvidence>(materializedEvidence);
        ParentSpecification = parentSpecification;
        IsDisabled = isDisabled;
        CanDeleteLocalHistory = canDeleteLocalHistory;
    }

    public SkillSpecContract Specification { get; }

    public SkillLifecycleState Lifecycle { get; }

    public string GrantedRootLabel { get; }

    public IReadOnlyList<GrantCapability> GrantedCapabilities { get; }

    public IReadOnlyList<SkillCardSample> Samples { get; }

    public IReadOnlyList<SkillCardEvidence> Evidence { get; }

    public SkillSpecContract? ParentSpecification { get; }

    public bool IsDisabled { get; }

    public bool CanDeleteLocalHistory { get; }
}

public sealed record SkillCardFactViewModel(string Label, string Value);

public sealed record SkillCardSampleViewModel(string Before, string After);

public sealed record SkillCardEvidenceViewModel(
    string Kind,
    string ReasonCode,
    string ObservedUtc,
    string SpecificationHash,
    string? ArtifactFingerprint,
    bool IsCurrentVersion);

public sealed record SkillCardSemanticChangeViewModel(
    string Field,
    SkillCardSemanticChangeKind Kind,
    string? Before,
    string? After);

public sealed record SkillCardActionViewModel
{
    public SkillCardActionViewModel(
        SkillCardActionCode code,
        string label,
        string automationName,
        bool isEnabled,
        string? disabledReason)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationName);
        if (isEnabled == !string.IsNullOrWhiteSpace(disabledReason))
        {
            throw new ArgumentException(
                "Enabled actions have no disabled reason; disabled actions require one.");
        }

        Code = code;
        Label = label;
        AutomationName = automationName;
        IsEnabled = isEnabled;
        DisabledReason = disabledReason;
    }

    public SkillCardActionCode Code { get; }

    public string Label { get; }

    public string AutomationName { get; }

    public bool IsEnabled { get; }

    public string? DisabledReason { get; }
}

public sealed class SkillCardViewModel : INotifyPropertyChanged
{
    private string editableName;

    internal SkillCardViewModel(
        string originalName,
        string plainLanguageSummary,
        string skillId,
        int version,
        string specificationHash,
        string lifecycle,
        string evidenceSummary,
        string invocation,
        string grantedRootLabel,
        string relativeScope,
        string grantedActions,
        IEnumerable<SkillCardFactViewModel> matchPredicates,
        IEnumerable<SkillCardFactViewModel> variables,
        IEnumerable<string> operations,
        IEnumerable<string> alwaysRules,
        IEnumerable<string> neverRules,
        IEnumerable<string> askRules,
        IEnumerable<string> successRules,
        IEnumerable<SkillCardFactViewModel> learnedFrom,
        IEnumerable<SkillCardFactViewModel> compatibility,
        IEnumerable<SkillCardSampleViewModel> samples,
        IEnumerable<SkillCardEvidenceViewModel> evidence,
        IEnumerable<SkillCardSemanticChangeViewModel> semanticDiff,
        IEnumerable<SkillCardActionViewModel> actions)
    {
        OriginalName = originalName;
        editableName = originalName;
        PlainLanguageSummary = plainLanguageSummary;
        SkillId = skillId;
        Version = version;
        SpecificationHash = specificationHash;
        Lifecycle = lifecycle;
        EvidenceSummary = evidenceSummary;
        Invocation = invocation;
        GrantedRootLabel = grantedRootLabel;
        RelativeScope = relativeScope;
        GrantedActions = grantedActions;
        MatchPredicates = ReadOnly(matchPredicates);
        Variables = ReadOnly(variables);
        Operations = ReadOnly(operations);
        AlwaysRules = ReadOnly(alwaysRules);
        NeverRules = ReadOnly(neverRules);
        AskRules = ReadOnly(askRules);
        SuccessRules = ReadOnly(successRules);
        LearnedFrom = ReadOnly(learnedFrom);
        Compatibility = ReadOnly(compatibility);
        Samples = ReadOnly(samples);
        Evidence = ReadOnly(evidence);
        SemanticDiff = ReadOnly(semanticDiff);
        Actions = ReadOnly(actions);
        RehearseAction = Action(SkillCardActionCode.Rehearse);
        ApproveAction = Action(SkillCardActionCode.Approve);
        DisableAction = Action(SkillCardActionCode.Disable);
        CorrectAction = Action(SkillCardActionCode.Correct);
        ExportAction = Action(SkillCardActionCode.Export);
        DeleteLocalHistoryAction = Action(SkillCardActionCode.DeleteLocalHistory);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OriginalName { get; }

    public string EditableName
    {
        get => editableName;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(editableName, next, StringComparison.Ordinal))
            {
                return;
            }

            editableName = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameValidationMessage));
            OnPropertyChanged(nameof(HasValidEditableName));
            OnPropertyChanged(nameof(HasUnsavedNameEdit));
        }
    }

    public string? NameValidationMessage =>
        string.IsNullOrWhiteSpace(editableName)
            ? "Name is required."
            : editableName.Length > 80
                ? "Name must be 80 characters or fewer."
                : editableName.Any(char.IsControl)
                    ? "Name cannot contain control characters."
                    : null;

    public bool HasValidEditableName => NameValidationMessage is null;

    public bool HasUnsavedNameEdit =>
        !string.Equals(OriginalName, EditableName, StringComparison.Ordinal);

    public string PlainLanguageSummary { get; }

    public string SkillId { get; }

    public int Version { get; }

    public string SpecificationHash { get; }

    public string Lifecycle { get; }

    public string EvidenceSummary { get; }

    public string Invocation { get; }

    public string GrantedRootLabel { get; }

    public string RelativeScope { get; }

    public string GrantedActions { get; }

    public IReadOnlyList<SkillCardFactViewModel> MatchPredicates { get; }

    public IReadOnlyList<SkillCardFactViewModel> Variables { get; }

    public IReadOnlyList<string> Operations { get; }

    public IReadOnlyList<string> AlwaysRules { get; }

    public IReadOnlyList<string> NeverRules { get; }

    public IReadOnlyList<string> AskRules { get; }

    public IReadOnlyList<string> SuccessRules { get; }

    public IReadOnlyList<SkillCardFactViewModel> LearnedFrom { get; }

    public IReadOnlyList<SkillCardFactViewModel> Compatibility { get; }

    public IReadOnlyList<SkillCardSampleViewModel> Samples { get; }

    public IReadOnlyList<SkillCardEvidenceViewModel> Evidence { get; }

    public IReadOnlyList<SkillCardSemanticChangeViewModel> SemanticDiff { get; }

    public IReadOnlyList<SkillCardActionViewModel> Actions { get; }

    public SkillCardActionViewModel RehearseAction { get; }

    public SkillCardActionViewModel ApproveAction { get; }

    public SkillCardActionViewModel DisableAction { get; }

    public SkillCardActionViewModel CorrectAction { get; }

    public SkillCardActionViewModel ExportAction { get; }

    public SkillCardActionViewModel DeleteLocalHistoryAction { get; }

    private static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());

    private SkillCardActionViewModel Action(SkillCardActionCode code) =>
        Actions.Single(action => action.Code == code);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

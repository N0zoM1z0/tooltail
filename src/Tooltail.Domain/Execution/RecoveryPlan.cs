using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;

namespace Tooltail.Domain.Execution;

public enum RecoveryPrimitive
{
    RenameBack,
    MoveBack,
    RemoveCreatedEntry,
}

public sealed record PlannedRecoveryOperation
{
    public PlannedRecoveryOperation(
        int sequence,
        int originalStepSequence,
        FilePrimitive originalPrimitive,
        RecoveryPrimitive primitive,
        string sourceRelativePath,
        string? destinationRelativePath,
        VerifiedEntryEvidence expectedSource,
        bool originalDestinationWasAbsent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(originalStepSequence, 1);
        if (!Enum.IsDefined(originalPrimitive) || !Enum.IsDefined(primitive))
        {
            throw new ArgumentOutOfRangeException(
                nameof(primitive),
                "Recovery operations must use the closed recovery vocabulary.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentNullException.ThrowIfNull(expectedSource);
        ValidateShape(
            originalPrimitive,
            primitive,
            destinationRelativePath,
            expectedSource,
            originalDestinationWasAbsent);

        Sequence = sequence;
        OriginalStepSequence = originalStepSequence;
        OriginalPrimitive = originalPrimitive;
        Primitive = primitive;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        ExpectedSource = expectedSource;
        OriginalDestinationWasAbsent = originalDestinationWasAbsent;
    }

    public int Sequence { get; }

    public int OriginalStepSequence { get; }

    public FilePrimitive OriginalPrimitive { get; }

    public RecoveryPrimitive Primitive { get; }

    public string SourceRelativePath { get; }

    public string? DestinationRelativePath { get; }

    public VerifiedEntryEvidence ExpectedSource { get; }

    public bool OriginalDestinationWasAbsent { get; }

    public GrantCapability RequiredCapability =>
        (Primitive, OriginalPrimitive) switch
        {
            (RecoveryPrimitive.RenameBack, FilePrimitive.RenameFile) =>
                GrantCapability.Rename,
            (RecoveryPrimitive.MoveBack, FilePrimitive.MoveFile) =>
                GrantCapability.MoveWithinRoot,
            (RecoveryPrimitive.RemoveCreatedEntry, FilePrimitive.EnsureDirectory) =>
                GrantCapability.CreateDirectory,
            (RecoveryPrimitive.RemoveCreatedEntry, FilePrimitive.CopyFile) =>
                GrantCapability.CopyWithinRoot,
            _ => throw new InvalidOperationException(
                "The recovery operation shape was not validated."),
        };

    private static void ValidateShape(
        FilePrimitive originalPrimitive,
        RecoveryPrimitive primitive,
        string? destinationRelativePath,
        VerifiedEntryEvidence expectedSource,
        bool originalDestinationWasAbsent)
    {
        if (!originalDestinationWasAbsent)
        {
            throw new ArgumentException(
                "Recovery requires proof that the original destination was absent.",
                nameof(originalDestinationWasAbsent));
        }

        bool relocation = primitive is RecoveryPrimitive.RenameBack or RecoveryPrimitive.MoveBack;
        if (relocation != (destinationRelativePath is not null))
        {
            throw new ArgumentException(
                "Only relocation recovery operations have a destination path.",
                nameof(destinationRelativePath));
        }

        if (destinationRelativePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
        }

        if (relocation)
        {
            bool matchingPair =
                (primitive == RecoveryPrimitive.RenameBack &&
                 originalPrimitive == FilePrimitive.RenameFile) ||
                (primitive == RecoveryPrimitive.MoveBack &&
                 originalPrimitive == FilePrimitive.MoveFile);
            if (!matchingPair ||
                expectedSource.Kind != VerifiedEntryKind.File ||
                expectedSource.ContentHash is null)
            {
                throw new ArgumentException(
                    "Relocation recovery requires matching original semantics and exact hashed file evidence.");
            }

            return;
        }

        bool removableCopy =
            originalPrimitive == FilePrimitive.CopyFile &&
            expectedSource.Kind == VerifiedEntryKind.File &&
            expectedSource.ContentHash is not null;
        bool removableDirectory =
            originalPrimitive == FilePrimitive.EnsureDirectory &&
            expectedSource.Kind == VerifiedEntryKind.Directory;
        if (!removableCopy && !removableDirectory)
        {
            throw new ArgumentException(
                "Created-entry removal requires an exact copied file or created directory proof.");
        }
    }
}

public sealed record RecoveryPlanDefinition
{
    public RecoveryPlanDefinition(
        PlanId id,
        ExecutionId originalExecutionId,
        PlanId originalPlanId,
        PlanFingerprint originalPlanFingerprint,
        SkillId skillId,
        SkillVersionNumber skillVersion,
        SkillSpecificationHash skillSpecificationHash,
        GrantId grantId,
        ResourceRootIdentity rootIdentity,
        IEnumerable<GrantCapability> grantedCapabilities,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc,
        IEnumerable<PlannedRecoveryOperation> operations)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(originalExecutionId.Value);
        IdentifierGuard.NotEmpty(originalPlanId.Value);
        IdentifierGuard.NotEmpty(skillId.Value);
        IdentifierGuard.NotEmpty(grantId.Value);
        ArgumentNullException.ThrowIfNull(originalPlanFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(skillVersion.Value, 1);
        ArgumentNullException.ThrowIfNull(skillSpecificationHash);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        ArgumentNullException.ThrowIfNull(operations);
        UtcGuard.RequireUtc(createdUtc, nameof(createdUtc));
        UtcGuard.RequireUtc(expiresUtc, nameof(expiresUtc));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresUtc, createdUtc);
        if (id == originalPlanId)
        {
            throw new ArgumentException(
                "A recovery plan must have an identity distinct from the original plan.",
                nameof(id));
        }

        GrantCapability[] materializedCapabilities = grantedCapabilities.Take(8).ToArray();
        FrozenSet<GrantCapability> capabilities = materializedCapabilities.ToFrozenSet();
        if (capabilities.Count == 0 ||
            materializedCapabilities.Length > 7 ||
            capabilities.Any(static capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentException(
                "A recovery plan must bind a non-empty closed grant capability set.",
                nameof(grantedCapabilities));
        }

        PlannedRecoveryOperation[] ordered = operations.Take(10_001).ToArray();
        if (ordered.Length is < 1 or > 10_000)
        {
            throw new ArgumentException(
                "A recovery plan must contain between one and 10,000 proven inverse operations.",
                nameof(operations));
        }

        HashSet<int> originalSteps = [];
        for (int index = 0; index < ordered.Length; index++)
        {
            PlannedRecoveryOperation operation = ordered[index];
            if (operation.Sequence != index + 1 ||
                !originalSteps.Add(operation.OriginalStepSequence))
            {
                throw new ArgumentException(
                    "Recovery steps must be contiguous and reference distinct original steps.",
                    nameof(operations));
            }

            if (!capabilities.Contains(operation.RequiredCapability))
            {
                throw new ArgumentException(
                    "Every recovery operation must remain covered by its original grant capability.",
                    nameof(grantedCapabilities));
            }
        }

        if (!capabilities.Contains(GrantCapability.Enumerate) ||
            !capabilities.Contains(GrantCapability.ReadMetadata) ||
            (ordered.Any(static operation =>
                 operation.ExpectedSource.Kind == VerifiedEntryKind.File) &&
             !capabilities.Contains(GrantCapability.ReadContentHash)))
        {
            throw new ArgumentException(
                "Recovery requires current enumeration, metadata, and applicable hash authority.",
                nameof(grantedCapabilities));
        }

        Id = id;
        OriginalExecutionId = originalExecutionId;
        OriginalPlanId = originalPlanId;
        OriginalPlanFingerprint = originalPlanFingerprint;
        SkillId = skillId;
        SkillVersion = skillVersion;
        SkillSpecificationHash = skillSpecificationHash;
        GrantId = grantId;
        RootIdentity = rootIdentity;
        GrantedCapabilities = capabilities;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
        Operations = new ReadOnlyCollection<PlannedRecoveryOperation>(ordered);
    }

    public PlanId Id { get; }

    public ExecutionId OriginalExecutionId { get; }

    public PlanId OriginalPlanId { get; }

    public PlanFingerprint OriginalPlanFingerprint { get; }

    public SkillId SkillId { get; }

    public SkillVersionNumber SkillVersion { get; }

    public SkillSpecificationHash SkillSpecificationHash { get; }

    public GrantId GrantId { get; }

    public ResourceRootIdentity RootIdentity { get; }

    public IReadOnlySet<GrantCapability> GrantedCapabilities { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public IReadOnlyList<PlannedRecoveryOperation> Operations { get; }
}

public sealed record RecoveryPlan
{
    public RecoveryPlan(RecoveryPlanDefinition definition, PlanFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fingerprint);
        Definition = definition;
        Fingerprint = fingerprint;
    }

    public RecoveryPlanDefinition Definition { get; }

    public PlanFingerprint Fingerprint { get; }
}

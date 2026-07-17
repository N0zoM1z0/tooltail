using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Permissions;
using Tooltail.Domain.Skills;

namespace Tooltail.Domain.Execution;

public enum FilePrimitive
{
    EnsureDirectory,
    RenameFile,
    MoveFile,
    CopyFile,
}

public enum DestinationPrecondition
{
    Absent,
    ExistingDirectory,
}

public enum ExpectedSourceState
{
    NotApplicable,
    Absent,
    Unchanged,
}

public enum ExpectedDestinationState
{
    DirectoryPresent,
    FileMatchesSource,
}

public sealed record SkillSpecificationHash
{
    public SkillSpecificationHash(string value)
    {
        Sha256Guard.Validate(value, nameof(value));
        Value = value;
    }

    public string Value { get; }
}

public sealed record ContentHash
{
    public ContentHash(string value)
    {
        Sha256Guard.Validate(value, nameof(value));
        Value = value;
    }

    public string Value { get; }
}

public sealed record PlanFingerprint
{
    public PlanFingerprint(string value)
    {
        Sha256Guard.Validate(value, nameof(value));
        Value = value;
    }

    public string Value { get; }
}

public sealed record SourceFileFingerprint
{
    public SourceFileFingerprint(
        string entryIdentity,
        long length,
        DateTimeOffset lastWriteUtc,
        ContentHash? contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        UtcGuard.RequireUtc(lastWriteUtc, nameof(lastWriteUtc));

        EntryIdentity = entryIdentity;
        Length = length;
        LastWriteUtc = lastWriteUtc;
        ContentHash = contentHash;
    }

    public string EntryIdentity { get; }

    public long Length { get; }

    public DateTimeOffset LastWriteUtc { get; }

    public ContentHash? ContentHash { get; }
}

public sealed record PlannedFileOperation
{
    public PlannedFileOperation(
        int sequence,
        FilePrimitive primitive,
        string? sourceRelativePath,
        string destinationRelativePath,
        SourceFileFingerprint? sourceFingerprint,
        DestinationPrecondition destinationPrecondition,
        ExpectedSourceState expectedSourceState,
        ExpectedDestinationState expectedDestinationState)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
        ValidateShape(
            primitive,
            sourceRelativePath,
            sourceFingerprint,
            destinationPrecondition,
            expectedSourceState,
            expectedDestinationState);

        Sequence = sequence;
        Primitive = primitive;
        SourceRelativePath = sourceRelativePath;
        DestinationRelativePath = destinationRelativePath;
        SourceFingerprint = sourceFingerprint;
        DestinationPrecondition = destinationPrecondition;
        ExpectedSourceState = expectedSourceState;
        ExpectedDestinationState = expectedDestinationState;
    }

    public int Sequence { get; }

    public FilePrimitive Primitive { get; }

    public string? SourceRelativePath { get; }

    public string DestinationRelativePath { get; }

    public SourceFileFingerprint? SourceFingerprint { get; }

    public DestinationPrecondition DestinationPrecondition { get; }

    public ExpectedSourceState ExpectedSourceState { get; }

    public ExpectedDestinationState ExpectedDestinationState { get; }

    private static void ValidateShape(
        FilePrimitive primitive,
        string? sourceRelativePath,
        SourceFileFingerprint? sourceFingerprint,
        DestinationPrecondition destinationPrecondition,
        ExpectedSourceState expectedSourceState,
        ExpectedDestinationState expectedDestinationState)
    {
        if (!Enum.IsDefined(primitive) ||
            !Enum.IsDefined(destinationPrecondition) ||
            !Enum.IsDefined(expectedSourceState) ||
            !Enum.IsDefined(expectedDestinationState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(primitive),
                "Plan enums must use the closed v0.1 vocabulary.");
        }

        if (primitive == FilePrimitive.EnsureDirectory)
        {
            if (sourceRelativePath is not null ||
                sourceFingerprint is not null ||
                destinationPrecondition != DestinationPrecondition.Absent ||
                expectedSourceState != ExpectedSourceState.NotApplicable ||
                expectedDestinationState != ExpectedDestinationState.DirectoryPresent)
            {
                throw new ArgumentException(
                    "An ensure-directory operation has no source and must exclusively create an absent directory.");
            }

            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        if (destinationPrecondition != DestinationPrecondition.Absent ||
            expectedDestinationState != ExpectedDestinationState.FileMatchesSource)
        {
            throw new ArgumentException("File destinations must be absent and must match the source after execution.");
        }

        ExpectedSourceState requiredSourceState = primitive == FilePrimitive.CopyFile
            ? ExpectedSourceState.Unchanged
            : ExpectedSourceState.Absent;
        if (expectedSourceState != requiredSourceState)
        {
            throw new ArgumentException("The expected source state does not match the primitive.");
        }
    }
}

public sealed record ExecutionPlanDefinition
{
    public ExecutionPlanDefinition(
        PlanId id,
        SkillId skillId,
        SkillVersionNumber skillVersion,
        SkillSpecificationHash skillSpecificationHash,
        GrantId grantId,
        ResourceRootIdentity rootIdentity,
        IEnumerable<GrantCapability> grantedCapabilities,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc,
        IEnumerable<PlannedFileOperation> operations)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(skillId.Value);
        IdentifierGuard.NotEmpty(grantId.Value);
        ArgumentOutOfRangeException.ThrowIfLessThan(skillVersion.Value, 1);
        ArgumentNullException.ThrowIfNull(skillSpecificationHash);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        ArgumentNullException.ThrowIfNull(operations);
        UtcGuard.RequireUtc(createdUtc, nameof(createdUtc));
        UtcGuard.RequireUtc(expiresUtc, nameof(expiresUtc));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresUtc, createdUtc);

        GrantCapability[] materializedCapabilities = grantedCapabilities.Take(8).ToArray();
        FrozenSet<GrantCapability> capabilities = materializedCapabilities.ToFrozenSet();
        if (capabilities.Count == 0 || materializedCapabilities.Length > 7)
        {
            throw new ArgumentException("A plan must bind a non-empty grant capability set.", nameof(grantedCapabilities));
        }

        if (capabilities.Any(static capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(grantedCapabilities),
                "Plan capabilities must use the closed v0.1 vocabulary.");
        }

        PlannedFileOperation[] orderedOperations = operations.Take(10_001).ToArray();
        if (orderedOperations.Length is < 1 or > 10_000)
        {
            throw new ArgumentException(
                "An execution plan must contain between one and 10,000 operations.",
                nameof(operations));
        }

        for (int index = 0; index < orderedOperations.Length; index++)
        {
            if (orderedOperations[index].Sequence != index + 1)
            {
                throw new ArgumentException(
                    "Plan operation sequences must be contiguous and order-defined.",
                    nameof(operations));
            }

            if (!capabilities.Contains(RequiredCapability(orderedOperations[index].Primitive)))
            {
                throw new ArgumentException(
                    "Every operation must be covered by the bound grant capability set.",
                    nameof(grantedCapabilities));
            }
        }

        Id = id;
        SkillId = skillId;
        SkillVersion = skillVersion;
        SkillSpecificationHash = skillSpecificationHash;
        GrantId = grantId;
        RootIdentity = rootIdentity;
        GrantedCapabilities = capabilities;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
        Operations = new ReadOnlyCollection<PlannedFileOperation>(orderedOperations);
    }

    public PlanId Id { get; }

    public SkillId SkillId { get; }

    public SkillVersionNumber SkillVersion { get; }

    public SkillSpecificationHash SkillSpecificationHash { get; }

    public GrantId GrantId { get; }

    public ResourceRootIdentity RootIdentity { get; }

    public IReadOnlySet<GrantCapability> GrantedCapabilities { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public IReadOnlyList<PlannedFileOperation> Operations { get; }

    private static GrantCapability RequiredCapability(FilePrimitive primitive) =>
        primitive switch
        {
            FilePrimitive.EnsureDirectory => GrantCapability.CreateDirectory,
            FilePrimitive.RenameFile => GrantCapability.Rename,
            FilePrimitive.MoveFile => GrantCapability.MoveWithinRoot,
            FilePrimitive.CopyFile => GrantCapability.CopyWithinRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
        };
}

public sealed record ExecutionPlan
{
    public ExecutionPlan(ExecutionPlanDefinition definition, PlanFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fingerprint);
        Definition = definition;
        Fingerprint = fingerprint;
    }

    public ExecutionPlanDefinition Definition { get; }

    public PlanFingerprint Fingerprint { get; }
}

internal static class Sha256Guard
{
    public static void Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(static character => !IsLowerHex(character)))
        {
            throw new ArgumentException("A SHA-256 digest must contain 64 lowercase hexadecimal characters.", parameterName);
        }
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

internal static class UtcGuard
{
    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Authoritative timestamps must use UTC.", parameterName);
        }
    }
}

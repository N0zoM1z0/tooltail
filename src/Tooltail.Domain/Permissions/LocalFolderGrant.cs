using System.Collections.Frozen;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Permissions;

public enum GrantCapability
{
    Enumerate,
    ReadMetadata,
    ReadContentHash,
    CreateDirectory,
    Rename,
    MoveWithinRoot,
    CopyWithinRoot,
}

public enum ResourceGrantState
{
    Active,
    Revoked,
    Expired,
}

public sealed record ResourceRootIdentity
{
    public ResourceRootIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

public sealed record LocalFolderGrant
{
    private LocalFolderGrant(
        GrantId id,
        CompanionId companionId,
        ResourceRootIdentity rootIdentity,
        FrozenSet<GrantCapability> capabilities,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt,
        ResourceGrantState state,
        DateTimeOffset? revokedAt,
        string? revocationReason)
    {
        Id = id;
        CompanionId = companionId;
        RootIdentity = rootIdentity;
        Capabilities = capabilities;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        State = state;
        RevokedAt = revokedAt;
        RevocationReason = revocationReason;
    }

    public GrantId Id { get; }

    public CompanionId CompanionId { get; }

    public ResourceRootIdentity RootIdentity { get; }

    public IReadOnlySet<GrantCapability> Capabilities { get; }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public ResourceGrantState State { get; private init; }

    public DateTimeOffset? RevokedAt { get; private init; }

    public string? RevocationReason { get; private init; }

    public static LocalFolderGrant Issue(
        GrantId id,
        CompanionId companionId,
        ResourceRootIdentity rootIdentity,
        IEnumerable<GrantCapability> capabilities,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt = null)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(companionId.Value);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(capabilities);

        FrozenSet<GrantCapability> capabilitySet = capabilities.ToFrozenSet();
        if (capabilitySet.Count == 0)
        {
            throw new ArgumentException("A resource grant must contain at least one capability.", nameof(capabilities));
        }

        if (capabilitySet.Any(static capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                "A resource grant can contain only known capabilities.");
        }

        if (expiresAt is not null && expiresAt <= issuedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        return new LocalFolderGrant(
            id,
            companionId,
            rootIdentity,
            capabilitySet,
            issuedAt,
            expiresAt,
            ResourceGrantState.Active,
            null,
            null);
    }

    public bool Allows(GrantCapability capability, DateTimeOffset now) =>
        State == ResourceGrantState.Active &&
        (ExpiresAt is null || now < ExpiresAt) &&
        Capabilities.Contains(capability);

    public DomainResult<LocalFolderGrant> Revoke(DateTimeOffset revokedAt, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (State != ResourceGrantState.Active)
        {
            return DomainResult.Failure<LocalFolderGrant>(
                "resource_grant.not_active",
                "Only an active resource grant can be revoked.");
        }

        if (revokedAt < IssuedAt)
        {
            return DomainResult.Failure<LocalFolderGrant>(
                "resource_grant.revocation_before_issue",
                "A resource grant cannot be revoked before it was issued.");
        }

        return DomainResult.Success(
            this with
            {
                State = ResourceGrantState.Revoked,
                RevokedAt = revokedAt,
                RevocationReason = reason,
            });
    }
}

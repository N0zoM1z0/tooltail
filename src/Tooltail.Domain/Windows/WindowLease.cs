using System.Collections.Frozen;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Windows;

public enum WindowLeaseState
{
    Active,
    Revoked,
    Expired,
}

public enum WindowLeaseRevocationReason
{
    UserRemovedPet,
    UserReturnedHome,
    UserRevoked,
    TargetDestroyed,
    TargetIdentityChanged,
    TargetIneligible,
    Expired,
    ApplicationShutdown,
}

public enum WindowContextCapability
{
    AnchorBody,
    PresentRunStatus,
    IdentifyTargetForUser,
}

public sealed record WindowTargetIdentity
{
    public WindowTargetIdentity(
        ulong windowHandle,
        ulong rootWindowHandle,
        int processId,
        DateTimeOffset processStartedAt,
        string applicationDisplayName,
        string? observedWindowTitle = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHandle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootWindowHandle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDisplayName);
        if (applicationDisplayName.Length > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(applicationDisplayName));
        }

        if (processStartedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A target process start time must use UTC.",
                nameof(processStartedAt));
        }

        if (observedWindowTitle?.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(observedWindowTitle));
        }

        WindowHandle = windowHandle;
        RootWindowHandle = rootWindowHandle;
        ProcessId = processId;
        ProcessStartedAt = processStartedAt;
        ApplicationDisplayName = applicationDisplayName;
        ObservedWindowTitle = string.IsNullOrEmpty(observedWindowTitle)
            ? null
            : observedWindowTitle;
    }

    public ulong WindowHandle { get; }

    public ulong RootWindowHandle { get; }

    public int ProcessId { get; }

    public DateTimeOffset ProcessStartedAt { get; }

    public string ApplicationDisplayName { get; }

    public string? ObservedWindowTitle { get; }

    public bool HasSameAuthorityIdentityAs(WindowTargetIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return WindowHandle == other.WindowHandle &&
            RootWindowHandle == other.RootWindowHandle &&
            ProcessId == other.ProcessId &&
            ProcessStartedAt == other.ProcessStartedAt;
    }
}

public sealed record WindowLease
{
    private WindowLease(
        LeaseId id,
        CompanionId companionId,
        WindowTargetIdentity target,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        FrozenSet<WindowContextCapability> contextCapabilities,
        WindowLeaseState state,
        DateTimeOffset? revokedAt,
        WindowLeaseRevocationReason? revocationReason)
    {
        Id = id;
        CompanionId = companionId;
        Target = target;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        ContextCapabilities = contextCapabilities;
        State = state;
        RevokedAt = revokedAt;
        RevocationReason = revocationReason;
    }

    public LeaseId Id { get; }

    public CompanionId CompanionId { get; }

    public WindowTargetIdentity Target { get; }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public IReadOnlySet<WindowContextCapability> ContextCapabilities { get; }

    public WindowLeaseState State { get; private init; }

    public DateTimeOffset? RevokedAt { get; private init; }

    public WindowLeaseRevocationReason? RevocationReason { get; private init; }

    public static WindowLease Issue(
        LeaseId id,
        CompanionId companionId,
        WindowTargetIdentity target,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        IEnumerable<WindowContextCapability>? contextCapabilities = null)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(companionId.Value);
        ArgumentNullException.ThrowIfNull(target);
        if (issuedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A window lease issue time must use UTC.",
                nameof(issuedAt));
        }

        if (expiresAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A window lease expiry time must use UTC.",
                nameof(expiresAt));
        }

        if (expiresAt <= issuedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A lease must expire after it is issued.");
        }

        if (target.ProcessStartedAt > issuedAt)
        {
            throw new ArgumentException(
                "The target process cannot start after the lease is issued.",
                nameof(target));
        }

        WindowContextCapability[] materializedCapabilities =
            (contextCapabilities ?? Enum.GetValues<WindowContextCapability>())
            .Take(4)
            .ToArray();
        FrozenSet<WindowContextCapability> capabilitySet =
            materializedCapabilities.ToFrozenSet();
        if (capabilitySet.Count == 0 ||
            materializedCapabilities.Length > 3 ||
            capabilitySet.Count != materializedCapabilities.Length)
        {
            throw new ArgumentException(
                "A window lease must contain between one and three unique context capabilities.",
                nameof(contextCapabilities));
        }

        if (capabilitySet.Any(static capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextCapabilities),
                "A window lease can contain only known context capabilities.");
        }

        return new WindowLease(
            id,
            companionId,
            target,
            issuedAt,
            expiresAt,
            capabilitySet,
            WindowLeaseState.Active,
            null,
            null);
    }

    public bool IsActiveAt(DateTimeOffset now) =>
        State == WindowLeaseState.Active &&
        now.Offset == TimeSpan.Zero &&
        now >= IssuedAt &&
        now < ExpiresAt;

    public DomainResult<WindowLease> Revoke(
        DateTimeOffset revokedAt,
        WindowLeaseRevocationReason reason)
    {
        if (State != WindowLeaseState.Active)
        {
            return DomainResult.Failure<WindowLease>(
                "window_lease.not_active",
                "Only an active window lease can be revoked.");
        }

        if (revokedAt.Offset != TimeSpan.Zero)
        {
            return DomainResult.Failure<WindowLease>(
                "window_lease.revocation_time_not_utc",
                "A window lease revocation time must use UTC.");
        }

        if (revokedAt < IssuedAt)
        {
            return DomainResult.Failure<WindowLease>(
                "window_lease.revocation_before_issue",
                "A window lease cannot be revoked before it was issued.");
        }

        if (reason == WindowLeaseRevocationReason.Expired && revokedAt < ExpiresAt)
        {
            return DomainResult.Failure<WindowLease>(
                "window_lease.expiry_before_deadline",
                "A window lease cannot expire before its deadline.");
        }

        if (reason != WindowLeaseRevocationReason.Expired && revokedAt >= ExpiresAt)
        {
            return DomainResult.Failure<WindowLease>(
                "window_lease.expired",
                "An elapsed window lease must be expired rather than otherwise revoked.");
        }

        WindowLeaseState nextState = reason == WindowLeaseRevocationReason.Expired
            ? WindowLeaseState.Expired
            : WindowLeaseState.Revoked;
        return DomainResult.Success(
            this with
            {
                State = nextState,
                RevokedAt = revokedAt,
                RevocationReason = reason,
            });
    }
}

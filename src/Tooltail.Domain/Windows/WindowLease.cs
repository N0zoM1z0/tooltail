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

public sealed record WindowTargetIdentity
{
    public WindowTargetIdentity(
        ulong windowHandle,
        ulong rootWindowHandle,
        int processId,
        DateTimeOffset processStartedAt,
        string applicationDisplayName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHandle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootWindowHandle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDisplayName);

        WindowHandle = windowHandle;
        RootWindowHandle = rootWindowHandle;
        ProcessId = processId;
        ProcessStartedAt = processStartedAt;
        ApplicationDisplayName = applicationDisplayName;
    }

    public ulong WindowHandle { get; }

    public ulong RootWindowHandle { get; }

    public int ProcessId { get; }

    public DateTimeOffset ProcessStartedAt { get; }

    public string ApplicationDisplayName { get; }
}

public sealed record WindowLease
{
    private WindowLease(
        LeaseId id,
        CompanionId companionId,
        WindowTargetIdentity target,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        WindowLeaseState state,
        DateTimeOffset? revokedAt,
        WindowLeaseRevocationReason? revocationReason)
    {
        Id = id;
        CompanionId = companionId;
        Target = target;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        State = state;
        RevokedAt = revokedAt;
        RevocationReason = revocationReason;
    }

    public LeaseId Id { get; }

    public CompanionId CompanionId { get; }

    public WindowTargetIdentity Target { get; }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public WindowLeaseState State { get; private init; }

    public DateTimeOffset? RevokedAt { get; private init; }

    public WindowLeaseRevocationReason? RevocationReason { get; private init; }

    public static WindowLease Issue(
        LeaseId id,
        CompanionId companionId,
        WindowTargetIdentity target,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (expiresAt <= issuedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A lease must expire after it is issued.");
        }

        return new WindowLease(
            id,
            companionId,
            target,
            issuedAt,
            expiresAt,
            WindowLeaseState.Active,
            null,
            null);
    }

    public bool IsActiveAt(DateTimeOffset now) => State == WindowLeaseState.Active && now < ExpiresAt;

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

        if (revokedAt < IssuedAt)
        {
            return DomainResult.Failure<WindowLease>(
                "window_lease.revocation_before_issue",
                "A window lease cannot be revoked before it was issued.");
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

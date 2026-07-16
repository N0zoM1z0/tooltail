using Tooltail.Contracts.Json;
using Tooltail.Contracts.Scopes;
using Tooltail.Domain.Windows;

namespace Tooltail.Application.Windows;

public static class WindowLeaseContractMapper
{
    public static WindowLeaseContract ToContract(WindowLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new WindowLeaseContract
        {
            SchemaVersion = ContractVersions.V1,
            LeaseId = lease.Id.Value,
            CompanionId = lease.CompanionId.Value,
            State = Map(lease.State),
            IssuedAt = lease.IssuedAt,
            ExpiresAt = lease.ExpiresAt,
            Target = new WindowTargetContract
            {
                Platform = WindowPlatform.Windows,
                Hwnd = FormatHandle(lease.Target.WindowHandle),
                RootHwnd = FormatHandle(lease.Target.RootWindowHandle),
                ProcessId = lease.Target.ProcessId,
                ProcessStartedAt = lease.Target.ProcessStartedAt,
                ApplicationDisplayName = lease.Target.ApplicationDisplayName,
                ObservedWindowTitle = lease.Target.ObservedWindowTitle,
            },
            ContextCapabilities = lease.ContextCapabilities
                .Order()
                .Select(Map)
                .ToArray(),
            Revocation = lease.RevokedAt is not null && lease.RevocationReason is not null
                ? new WindowLeaseRevocationContract
                {
                    At = lease.RevokedAt.Value,
                    Reason = Map(lease.RevocationReason.Value),
                }
                : null,
        };
    }

    private static string FormatHandle(ulong handle) => $"0x{handle:X}";

    private static WindowLeaseContractState Map(WindowLeaseState state) => state switch
    {
        WindowLeaseState.Active => WindowLeaseContractState.Active,
        WindowLeaseState.Revoked => WindowLeaseContractState.Revoked,
        WindowLeaseState.Expired => WindowLeaseContractState.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static Tooltail.Contracts.Scopes.WindowContextCapability Map(
        Tooltail.Domain.Windows.WindowContextCapability capability) => capability switch
        {
            Tooltail.Domain.Windows.WindowContextCapability.AnchorBody =>
                Tooltail.Contracts.Scopes.WindowContextCapability.AnchorBody,
            Tooltail.Domain.Windows.WindowContextCapability.PresentRunStatus =>
                Tooltail.Contracts.Scopes.WindowContextCapability.PresentRunStatus,
            Tooltail.Domain.Windows.WindowContextCapability.IdentifyTargetForUser =>
                Tooltail.Contracts.Scopes.WindowContextCapability.IdentifyTargetForUser,
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };

    private static WindowLeaseRevocationContractReason Map(
        WindowLeaseRevocationReason reason) => reason switch
        {
            WindowLeaseRevocationReason.UserRemovedPet =>
                WindowLeaseRevocationContractReason.UserRemovedPet,
            WindowLeaseRevocationReason.UserReturnedHome =>
                WindowLeaseRevocationContractReason.UserReturnedHome,
            WindowLeaseRevocationReason.UserRevoked =>
                WindowLeaseRevocationContractReason.UserRevoked,
            WindowLeaseRevocationReason.TargetDestroyed =>
                WindowLeaseRevocationContractReason.TargetDestroyed,
            WindowLeaseRevocationReason.TargetIdentityChanged =>
                WindowLeaseRevocationContractReason.TargetIdentityChanged,
            WindowLeaseRevocationReason.TargetIneligible =>
                WindowLeaseRevocationContractReason.TargetIneligible,
            WindowLeaseRevocationReason.Expired =>
                WindowLeaseRevocationContractReason.Expired,
            WindowLeaseRevocationReason.ApplicationShutdown =>
                WindowLeaseRevocationContractReason.ApplicationShutdown,
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
}

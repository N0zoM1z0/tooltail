using System.Globalization;
using Tooltail.Contracts.Json;

namespace Tooltail.Contracts.Scopes;

internal static class WindowLeaseContractValidator
{
    internal static ContractParseResult<WindowLeaseContract> Validate(
        WindowLeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.LeaseId == Guid.Empty ||
            lease.CompanionId == Guid.Empty ||
            !Enum.IsDefined(lease.State) ||
            lease.IssuedAt.Offset != TimeSpan.Zero ||
            lease.ExpiresAt.Offset != TimeSpan.Zero ||
            lease.ExpiresAt <= lease.IssuedAt ||
            !IsValidTarget(lease.Target, lease.IssuedAt) ||
            !HasValidCapabilities(lease.ContextCapabilities) ||
            !HasConsistentTerminalState(lease))
        {
            return ContractParseResult.Failure<WindowLeaseContract>(
                "contract.invalid_window_lease",
                "The window lease violates its closed identity or lifecycle rules.");
        }

        return ContractParseResult.Success(lease);
    }

    private static bool IsValidTarget(
        WindowTargetContract? target,
        DateTimeOffset issuedAt) =>
        target is not null &&
        Enum.IsDefined(target.Platform) &&
        TryParseHandle(target.Hwnd, out _) &&
        TryParseHandle(target.RootHwnd, out _) &&
        target.ProcessId > 0 &&
        target.ProcessStartedAt.Offset == TimeSpan.Zero &&
        target.ProcessStartedAt <= issuedAt &&
        !string.IsNullOrWhiteSpace(target.ApplicationDisplayName) &&
        target.ApplicationDisplayName.Length <= 120 &&
        (target.ObservedWindowTitle is null || target.ObservedWindowTitle.Length <= 256);

    private static bool HasValidCapabilities(
        IReadOnlyList<WindowContextCapability>? capabilities)
    {
        if (capabilities is null || capabilities.Count is < 1 or > 3)
        {
            return false;
        }

        HashSet<WindowContextCapability> unique = [];
        return capabilities.All(capability =>
            Enum.IsDefined(capability) && unique.Add(capability));
    }

    private static bool HasConsistentTerminalState(WindowLeaseContract lease)
    {
        if (lease.State == WindowLeaseContractState.Active)
        {
            return lease.Revocation is null;
        }

        WindowLeaseRevocationContract? revocation = lease.Revocation;
        if (revocation is null ||
            !Enum.IsDefined(revocation.Reason) ||
            revocation.At.Offset != TimeSpan.Zero ||
            revocation.At < lease.IssuedAt)
        {
            return false;
        }

        return lease.State switch
        {
            WindowLeaseContractState.Expired =>
                revocation.Reason == WindowLeaseRevocationContractReason.Expired &&
                revocation.At >= lease.ExpiresAt,
            WindowLeaseContractState.Revoked =>
                revocation.Reason != WindowLeaseRevocationContractReason.Expired &&
                revocation.At < lease.ExpiresAt,
            _ => false,
        };
    }

    private static bool TryParseHandle(string? value, out ulong handle)
    {
        handle = 0;
        return value is { Length: >= 3 and <= 18 } &&
            value.StartsWith("0x", StringComparison.Ordinal) &&
            ulong.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out handle) &&
            handle != 0;
    }
}

using System.Text.Json.Serialization;
using Tooltail.Contracts.Json;

namespace Tooltail.Contracts.Scopes;

public sealed record WindowLeaseContract : IVersionedContract
{
    public required string SchemaVersion { get; init; }

    public required Guid LeaseId { get; init; }

    public required Guid CompanionId { get; init; }

    public required WindowLeaseContractState State { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required WindowTargetContract Target { get; init; }

    public required IReadOnlyList<WindowContextCapability> ContextCapabilities { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowLeaseRevocationContract? Revocation { get; init; }
}

public enum WindowLeaseContractState
{
    Active,
    Revoked,
    Expired,
}

public sealed record WindowTargetContract
{
    public required WindowPlatform Platform { get; init; }

    public required string Hwnd { get; init; }

    public required string RootHwnd { get; init; }

    public required int ProcessId { get; init; }

    public required DateTimeOffset ProcessStartedAt { get; init; }

    public required string ApplicationDisplayName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedWindowTitle { get; init; }
}

public enum WindowPlatform
{
    Windows,
}

public enum WindowContextCapability
{
    AnchorBody,
    PresentRunStatus,
    IdentifyTargetForUser,
}

public sealed record WindowLeaseRevocationContract
{
    public required DateTimeOffset At { get; init; }

    public required WindowLeaseRevocationContractReason Reason { get; init; }
}

public enum WindowLeaseRevocationContractReason
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

using Tooltail.Application.Windows;
using Tooltail.Domain.Windows;

namespace Tooltail.Application.Abstractions;

/// <summary>
/// Provides bounded, read-only observations of native windows. Implementations never issue
/// leases or resource authority.
/// </summary>
public interface IWindowSystem
{
    ValueTask<WindowDiscoveryResult> DiscoverAtAsync(
        PhysicalScreenPoint point,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WindowTargetSnapshot>> EnumerateEligibleTargetsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask<WindowTargetObservation> ObserveAsync(
        WindowTargetIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    IWindowTargetMonitor Watch(WindowTargetIdentity targetIdentity);
}

/// <summary>
/// Owns one native target subscription. Implementations must bound and serialize callback
/// signals and release every native registration when disposed.
/// </summary>
public interface IWindowTargetMonitor : IAsyncDisposable
{
    IAsyncEnumerable<WindowTargetSignal> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

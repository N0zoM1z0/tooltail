using Tooltail.Application.Abstractions;
using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Windows;

namespace Tooltail.Application.Windows;

/// <summary>
/// Serializes preview, explicit lease issue, target revalidation, expiry, and revocation.
/// The native window system supplies observations only and cannot create authority.
/// </summary>
public sealed class WindowBindingService : IAsyncDisposable
{
    private readonly IWindowSystem windowSystem;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private readonly WindowBindingPolicy policy;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WindowBindingSnapshot current = WindowBindingSnapshot.AtHome;
    private MonitorRegistration? activeMonitor;
    private long operationVersion;
    private long stateRevision;
    private bool disposed;

    public WindowBindingService(
        IWindowSystem windowSystem,
        IClock clock,
        IIdGenerator idGenerator,
        WindowBindingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(windowSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.windowSystem = windowSystem;
        this.clock = clock;
        this.idGenerator = idGenerator;
        this.policy = policy ?? WindowBindingPolicy.Default;
    }

    public event EventHandler<WindowBindingChangedEventArgs>? Changed;

    public WindowBindingSnapshot Current => Volatile.Read(ref current);

    public async ValueTask<WindowBindingActionResult> BeginDragAsync(
        CancellationToken cancellationToken = default)
    {
        MonitorRegistration? detached = null;
        WindowBindingSnapshot next;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            operationVersion++;
            WindowLease? lease = current.Lease;
            string reasonCode = "window_binding.drag_started";
            if (lease?.State == WindowLeaseState.Active)
            {
                DateTimeOffset now = RequireUtcNow();
                WindowLeaseRevocationReason reason = now >= lease.ExpiresAt
                    ? WindowLeaseRevocationReason.Expired
                    : WindowLeaseRevocationReason.UserRemovedPet;
                lease = RevokeActiveLease(lease, now, reason);
                reasonCode = ReasonCode(reason);
                detached = DetachMonitor();
            }

            next = SetCurrent(
                WindowBindingMode.Dragging,
                preview: null,
                lease,
                observedTarget: null,
                reasonCode);
        }
        finally
        {
            gate.Release();
        }

        Publish(next);
        await StopMonitorAsync(detached).ConfigureAwait(false);
        return WindowBindingActionResult.Success(next);
    }

    public async ValueTask<WindowBindingActionResult> PreviewAtAsync(
        PhysicalScreenPoint physicalPoint,
        CancellationToken cancellationToken = default)
    {
        long version;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            if (current.Mode is not (
                WindowBindingMode.Dragging or
                WindowBindingMode.PreviewEligible or
                WindowBindingMode.PreviewIneligible))
            {
                return Failure("window_binding.drag_not_started");
            }

            version = ++operationVersion;
        }
        finally
        {
            gate.Release();
        }

        WindowDiscoveryResult discovery;
        try
        {
            discovery = await windowSystem
                .DiscoverAtAsync(physicalPoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedObservationException(exception))
        {
            discovery = WindowDiscoveryResult.Ineligible("window_target.discovery_failed");
        }

        WindowBindingSnapshot next;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || version != operationVersion)
            {
                return Failure("window_binding.stale_preview");
            }

            WindowBindingMode mode = discovery.Status == WindowDiscoveryStatus.Eligible
                ? WindowBindingMode.PreviewEligible
                : WindowBindingMode.PreviewIneligible;
            next = SetCurrent(
                mode,
                discovery,
                current.Lease,
                discovery.Target,
                discovery.ReasonCode);
        }
        finally
        {
            gate.Release();
        }

        Publish(next);
        return WindowBindingActionResult.Success(next);
    }

    public async ValueTask<WindowBindingActionResult> DropAsync(
        CompanionId companionId,
        CancellationToken cancellationToken = default)
    {
        WindowTargetSnapshot candidate;
        long version;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            if (current.Mode != WindowBindingMode.PreviewEligible ||
                current.Preview?.Target is not { } previewTarget)
            {
                return Failure("window_binding.no_eligible_preview");
            }

            candidate = previewTarget;
            version = ++operationVersion;
        }
        finally
        {
            gate.Release();
        }

        return await IssueAsync(
            companionId,
            candidate,
            version,
            WindowBindingMode.PreviewEligible,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<WindowTargetSnapshot>>
        EnumerateKeyboardTargetsAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return [];
        }

        IReadOnlyList<WindowTargetSnapshot> targets = await windowSystem
            .EnumerateEligibleTargetsAsync(policy.MaximumKeyboardTargets, cancellationToken)
            .ConfigureAwait(false);
        List<WindowTargetSnapshot> bounded = new(policy.MaximumKeyboardTargets);
        foreach (WindowTargetSnapshot target in targets)
        {
            if (bounded.Any(existing =>
                    existing.Identity.HasSameAuthorityIdentityAs(target.Identity)))
            {
                continue;
            }

            bounded.Add(target);
            if (bounded.Count == policy.MaximumKeyboardTargets)
            {
                break;
            }
        }

        return bounded;
    }

    public async ValueTask<WindowBindingActionResult> AttachFromKeyboardAsync(
        CompanionId companionId,
        WindowTargetSnapshot selectedTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedTarget);
        long version;
        WindowBindingSnapshot selecting;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            if (current.HasActiveLease)
            {
                return Failure("window_binding.active_lease_exists");
            }

            version = ++operationVersion;
            selecting = SetCurrent(
                WindowBindingMode.PreviewEligible,
                WindowDiscoveryResult.Eligible(selectedTarget),
                current.Lease,
                selectedTarget,
                "window_binding.keyboard_target_selected");
        }
        finally
        {
            gate.Release();
        }

        Publish(selecting);
        return await IssueAsync(
            companionId,
            selectedTarget,
            version,
            WindowBindingMode.PreviewEligible,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WindowBindingActionResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        MonitorRegistration? registration;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            registration = activeMonitor;
            if (registration is null || !current.HasActiveLease)
            {
                return Failure("window_binding.no_active_lease");
            }
        }
        finally
        {
            gate.Release();
        }

        return await ReconcileAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<WindowBindingActionResult> RevokeByUserAsync(
        CancellationToken cancellationToken = default) =>
        RevokeAsync(
            WindowLeaseRevocationReason.UserRevoked,
            "window_lease.user_revoked",
            moveHome: false,
            cancellationToken);

    public ValueTask<WindowBindingActionResult> ReturnHomeAsync(
        CancellationToken cancellationToken = default) =>
        RevokeAsync(
            WindowLeaseRevocationReason.UserReturnedHome,
            "window_lease.user_returned_home",
            moveHome: true,
            cancellationToken);

    public async ValueTask<WindowBindingActionResult> CancelDragAsync(
        CancellationToken cancellationToken = default)
    {
        WindowBindingSnapshot next;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            operationVersion++;
            next = SetCurrent(
                WindowBindingMode.Home,
                preview: null,
                current.Lease,
                observedTarget: null,
                "window_binding.drag_cancelled");
        }
        finally
        {
            gate.Release();
        }

        Publish(next);
        return WindowBindingActionResult.Success(next);
    }

    public async ValueTask DisposeAsync()
    {
        MonitorRegistration? detached;
        WindowBindingSnapshot? next = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            operationVersion++;
            WindowLease? lease = current.Lease;
            if (lease?.State == WindowLeaseState.Active)
            {
                DateTimeOffset now = RequireUtcNow();
                WindowLeaseRevocationReason reason = now >= lease.ExpiresAt
                    ? WindowLeaseRevocationReason.Expired
                    : WindowLeaseRevocationReason.ApplicationShutdown;
                lease = RevokeActiveLease(lease, now, reason);
                next = SetCurrent(
                    WindowBindingMode.Home,
                    preview: null,
                    lease,
                    observedTarget: null,
                    ReasonCode(reason));
            }

            detached = DetachMonitor();
            disposed = true;
        }
        finally
        {
            gate.Release();
        }

        if (next is not null)
        {
            Publish(next);
        }

        await StopMonitorAsync(detached).ConfigureAwait(false);
        gate.Dispose();
    }

    private async ValueTask<WindowBindingActionResult> IssueAsync(
        CompanionId companionId,
        WindowTargetSnapshot candidate,
        long expectedVersion,
        WindowBindingMode expectedMode,
        CancellationToken cancellationToken)
    {
        WindowTargetObservation observation;
        try
        {
            observation = await windowSystem
                .ObserveAsync(candidate.Identity, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedObservationException(exception))
        {
            observation = WindowTargetObservation.Ineligible("window_target.observation_failed");
        }

        if (observation.Status != WindowTargetObservationStatus.Valid ||
            observation.Target is not { } observed)
        {
            return await RejectIssueAsync(
                expectedVersion,
                observation.ReasonCode,
                cancellationToken).ConfigureAwait(false);
        }

        if (!candidate.Identity.HasSameAuthorityIdentityAs(observed.Identity))
        {
            return await RejectIssueAsync(
                expectedVersion,
                "window_target.identity_changed",
                cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset issuedAt = RequireUtcNow();
        WindowLease preparedLease = WindowLease.Issue(
            new LeaseId(idGenerator.NewId()),
            companionId,
            observed.Identity,
            issuedAt,
            issuedAt.Add(policy.LeaseLifetime));

        IWindowTargetMonitor monitor;
        try
        {
            monitor = windowSystem.Watch(observed.Identity);
            if (monitor is null)
            {
                throw new InvalidOperationException("The window monitor factory returned null.");
            }
        }
        catch (Exception exception) when (IsExpectedObservationException(exception))
        {
            return await RejectIssueAsync(
                expectedVersion,
                "window_target.monitor_unavailable",
                cancellationToken).ConfigureAwait(false);
        }

        WindowBindingSnapshot? next = null;
        MonitorRegistration? registration = null;
        string? failureCode = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || operationVersion != expectedVersion || current.Mode != expectedMode)
            {
                failureCode = "window_binding.stale_drop";
            }
            else if (current.HasActiveLease)
            {
                failureCode = "window_binding.active_lease_exists";
            }
            else
            {
                registration = new MonitorRegistration(preparedLease.Id, monitor);
                activeMonitor = registration;
                next = SetCurrent(
                    observed.IsMinimized
                        ? WindowBindingMode.TargetMinimized
                        : WindowBindingMode.Bound,
                    preview: null,
                    preparedLease,
                    observed,
                    observed.IsMinimized
                        ? "window_lease.target_minimized"
                        : "window_lease.issued");
                registration.Completion = Task.Run(
                    () => PumpMonitorAsync(registration),
                    CancellationToken.None);
            }
        }
        finally
        {
            gate.Release();
        }

        if (failureCode is not null)
        {
            await monitor.DisposeAsync().ConfigureAwait(false);
            return Failure(failureCode);
        }

        Publish(next!);
        return WindowBindingActionResult.Success(next!);
    }

    private async ValueTask<WindowBindingActionResult> RejectIssueAsync(
        long expectedVersion,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        WindowBindingSnapshot? next = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!disposed && operationVersion == expectedVersion)
            {
                next = SetCurrent(
                    WindowBindingMode.PreviewIneligible,
                    WindowDiscoveryResult.Ineligible(reasonCode),
                    current.Lease,
                    observedTarget: null,
                    reasonCode);
            }
        }
        finally
        {
            gate.Release();
        }

        if (next is not null)
        {
            Publish(next);
            return WindowBindingActionResult.Failure(next, reasonCode);
        }

        return Failure("window_binding.stale_drop");
    }

    private async Task PumpMonitorAsync(MonitorRegistration registration)
    {
        string? terminalFailure = null;
        try
        {
            await foreach (WindowTargetSignal signal in registration.Monitor
                .ReadAllAsync(registration.Cancellation.Token)
                .WithCancellation(registration.Cancellation.Token)
                .ConfigureAwait(false))
            {
                bool keepWatching = await ProcessSignalAsync(registration, signal)
                    .ConfigureAwait(false);
                if (!keepWatching)
                {
                    break;
                }
            }

            if (!registration.Cancellation.IsCancellationRequested)
            {
                terminalFailure = "window_target.monitor_disconnected";
            }
        }
        catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedObservationException(exception))
        {
            terminalFailure = "window_target.monitor_failed";
        }
        finally
        {
            if (terminalFailure is not null)
            {
                await RevokeFromMonitorAsync(
                    registration,
                    WindowLeaseRevocationReason.TargetIneligible,
                    terminalFailure).ConfigureAwait(false);
            }

            await registration.DisposeResourcesAsync().ConfigureAwait(false);
            registration.DisposeCancellation();
        }
    }

    private async ValueTask<bool> ProcessSignalAsync(
        MonitorRegistration registration,
        WindowTargetSignal signal)
    {
        WindowLease? lease = Current.Lease;
        if (lease is null || lease.Id != registration.LeaseId)
        {
            return false;
        }

        if (signal.WindowHandle != 0 &&
            signal.WindowHandle != lease.Target.WindowHandle &&
            signal.WindowHandle != lease.Target.RootWindowHandle)
        {
            return true;
        }

        switch (signal.Kind)
        {
            case WindowTargetSignalKind.Destroyed:
            case WindowTargetSignalKind.ProcessExited:
                await RevokeFromMonitorAsync(
                    registration,
                    WindowLeaseRevocationReason.TargetDestroyed,
                    "window_target.destroyed").ConfigureAwait(false);
                return false;
            case WindowTargetSignalKind.Cloaked:
                await RevokeFromMonitorAsync(
                    registration,
                    WindowLeaseRevocationReason.TargetIneligible,
                    "window_target.cloaked").ConfigureAwait(false);
                return false;
            default:
                WindowBindingActionResult result = await ReconcileAsync(
                    registration,
                    registration.Cancellation.Token).ConfigureAwait(false);
                return result.Snapshot.HasActiveLease;
        }
    }

    private async ValueTask<WindowBindingActionResult> ReconcileAsync(
        MonitorRegistration registration,
        CancellationToken cancellationToken)
    {
        WindowLease lease;
        WindowBindingSnapshot? elapsed = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || activeMonitor != registration ||
                current.Lease is not { State: WindowLeaseState.Active } activeLease)
            {
                return Failure("window_binding.no_active_lease");
            }

            lease = activeLease;
            DateTimeOffset now = RequireUtcNow();
            if (now >= lease.ExpiresAt)
            {
                WindowLease expired = RevokeActiveLease(
                    lease,
                    now,
                    WindowLeaseRevocationReason.Expired);
                activeMonitor = null;
                elapsed = SetCurrent(
                    WindowBindingMode.Expired,
                    preview: null,
                    expired,
                    observedTarget: null,
                    "window_lease.expired");
            }
        }
        finally
        {
            gate.Release();
        }

        if (elapsed is not null)
        {
            registration.Cancellation.Cancel();
            Publish(elapsed);
            return WindowBindingActionResult.Success(elapsed);
        }

        WindowTargetObservation observation;
        try
        {
            observation = await windowSystem
                .ObserveAsync(lease.Target, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedObservationException(exception))
        {
            observation = WindowTargetObservation.Ineligible("window_target.observation_failed");
        }

        if (observation.Status != WindowTargetObservationStatus.Valid ||
            observation.Target is not { } observed)
        {
            (WindowLeaseRevocationReason reason, string code) = observation.Status switch
            {
                WindowTargetObservationStatus.Destroyed =>
                    (WindowLeaseRevocationReason.TargetDestroyed, observation.ReasonCode),
                WindowTargetObservationStatus.IdentityChanged =>
                    (WindowLeaseRevocationReason.TargetIdentityChanged, observation.ReasonCode),
                _ => (WindowLeaseRevocationReason.TargetIneligible, observation.ReasonCode),
            };
            await RevokeFromMonitorAsync(registration, reason, code).ConfigureAwait(false);
            return WindowBindingActionResult.Failure(Current, code);
        }

        if (!lease.Target.HasSameAuthorityIdentityAs(observed.Identity))
        {
            const string identityChanged = "window_target.identity_changed";
            await RevokeFromMonitorAsync(
                registration,
                WindowLeaseRevocationReason.TargetIdentityChanged,
                identityChanged).ConfigureAwait(false);
            return WindowBindingActionResult.Failure(Current, identityChanged);
        }

        WindowBindingSnapshot next;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || activeMonitor != registration || !current.HasActiveLease)
            {
                return Failure("window_binding.no_active_lease");
            }

            next = SetCurrent(
                observed.IsMinimized
                    ? WindowBindingMode.TargetMinimized
                    : WindowBindingMode.Bound,
                preview: null,
                current.Lease,
                observed,
                observed.IsMinimized
                    ? "window_lease.target_minimized"
                    : "window_lease.reconciled");
        }
        finally
        {
            gate.Release();
        }

        Publish(next);
        return WindowBindingActionResult.Success(next);
    }

    private async ValueTask<WindowBindingActionResult> RevokeAsync(
        WindowLeaseRevocationReason reason,
        string reasonCode,
        bool moveHome,
        CancellationToken cancellationToken)
    {
        MonitorRegistration? detached;
        WindowBindingSnapshot next;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Failure("window_binding.disposed");
            }

            operationVersion++;
            WindowLease? lease = current.Lease;
            if (lease?.State != WindowLeaseState.Active)
            {
                if (!moveHome)
                {
                    return Failure("window_binding.no_active_lease");
                }

                next = SetCurrent(
                    WindowBindingMode.Home,
                    preview: null,
                    lease,
                    observedTarget: null,
                    reasonCode);
                detached = DetachMonitor();
            }
            else
            {
                DateTimeOffset now = RequireUtcNow();
                WindowLeaseRevocationReason effectiveReason = now >= lease.ExpiresAt
                    ? WindowLeaseRevocationReason.Expired
                    : reason;
                lease = RevokeActiveLease(lease, now, effectiveReason);
                next = SetCurrent(
                    moveHome
                        ? WindowBindingMode.Home
                        : effectiveReason == WindowLeaseRevocationReason.Expired
                            ? WindowBindingMode.Expired
                            : WindowBindingMode.Revoked,
                    preview: null,
                    lease,
                    observedTarget: null,
                    effectiveReason == reason ? reasonCode : ReasonCode(effectiveReason));
                detached = DetachMonitor();
            }
        }
        finally
        {
            gate.Release();
        }

        Publish(next);
        await StopMonitorAsync(detached).ConfigureAwait(false);
        return WindowBindingActionResult.Success(next);
    }

    private async ValueTask RevokeFromMonitorAsync(
        MonitorRegistration registration,
        WindowLeaseRevocationReason reason,
        string reasonCode)
    {
        WindowBindingSnapshot? next = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed || activeMonitor != registration ||
                current.Lease is not { State: WindowLeaseState.Active } lease)
            {
                return;
            }

            DateTimeOffset now = RequireUtcNow();
            WindowLeaseRevocationReason effectiveReason = now >= lease.ExpiresAt
                ? WindowLeaseRevocationReason.Expired
                : reason;
            WindowLease revoked = RevokeActiveLease(lease, now, effectiveReason);
            activeMonitor = null;
            next = SetCurrent(
                effectiveReason == WindowLeaseRevocationReason.Expired
                    ? WindowBindingMode.Expired
                    : WindowBindingMode.Revoked,
                preview: null,
                revoked,
                observedTarget: null,
                effectiveReason == reason ? reasonCode : ReasonCode(effectiveReason));
        }
        finally
        {
            gate.Release();
        }

        if (next is not null)
        {
            registration.Cancellation.Cancel();
            Publish(next);
        }
    }

    private static WindowLease RevokeActiveLease(
        WindowLease lease,
        DateTimeOffset revokedAt,
        WindowLeaseRevocationReason reason)
    {
        DomainResult<WindowLease> result = lease.Revoke(revokedAt, reason);
        return result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException(
                $"The committed lease transition was invalid: {result.Error?.Code}");
    }

    private MonitorRegistration? DetachMonitor()
    {
        MonitorRegistration? detached = activeMonitor;
        activeMonitor = null;
        return detached;
    }

    private static async ValueTask StopMonitorAsync(MonitorRegistration? registration)
    {
        if (registration is null)
        {
            return;
        }

        await registration.DisposeResourcesAsync().ConfigureAwait(false);
        await registration.Completion.ConfigureAwait(false);
        registration.DisposeCancellation();
    }

    private WindowBindingSnapshot SetCurrent(
        WindowBindingMode mode,
        WindowDiscoveryResult? preview,
        WindowLease? lease,
        WindowTargetSnapshot? observedTarget,
        string reasonCode)
    {
        WindowBindingSnapshot next = new(
            mode,
            preview,
            lease,
            observedTarget,
            reasonCode,
            checked(++stateRevision));
        Volatile.Write(ref current, next);
        return next;
    }

    private void Publish(WindowBindingSnapshot snapshot)
    {
        if (Current.Revision == snapshot.Revision)
        {
            Changed?.Invoke(this, new WindowBindingChangedEventArgs(snapshot));
        }
    }

    private WindowBindingActionResult Failure(string reasonCode) =>
        WindowBindingActionResult.Failure(Current, reasonCode);

    private DateTimeOffset RequireUtcNow()
    {
        DateTimeOffset now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero
            ? now
            : throw new InvalidOperationException("The application clock must supply UTC time.");
    }

    private static string ReasonCode(WindowLeaseRevocationReason reason) => reason switch
    {
        WindowLeaseRevocationReason.UserRemovedPet => "window_lease.user_removed_pet",
        WindowLeaseRevocationReason.UserReturnedHome => "window_lease.user_returned_home",
        WindowLeaseRevocationReason.UserRevoked => "window_lease.user_revoked",
        WindowLeaseRevocationReason.TargetDestroyed => "window_target.destroyed",
        WindowLeaseRevocationReason.TargetIdentityChanged => "window_target.identity_changed",
        WindowLeaseRevocationReason.TargetIneligible => "window_target.ineligible",
        WindowLeaseRevocationReason.Expired => "window_lease.expired",
        WindowLeaseRevocationReason.ApplicationShutdown => "window_lease.application_shutdown",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static bool IsExpectedObservationException(Exception exception) =>
        exception is InvalidOperationException or
            NotSupportedException or
            UnauthorizedAccessException or
            IOException or
            System.ComponentModel.Win32Exception;

    private sealed class MonitorRegistration(
        LeaseId leaseId,
        IWindowTargetMonitor monitor)
    {
        private int resourcesDisposed;
        private int cancellationDisposed;

        public LeaseId LeaseId { get; } = leaseId;

        public IWindowTargetMonitor Monitor { get; } = monitor;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion { get; set; } = Task.CompletedTask;

        public async ValueTask DisposeResourcesAsync()
        {
            if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0)
            {
                return;
            }

            Cancellation.Cancel();
            await Monitor.DisposeAsync().ConfigureAwait(false);
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref cancellationDisposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }
}

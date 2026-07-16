using System.Text;
using System.Threading.Channels;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Windows;

namespace Tooltail.Application.Tests;

public sealed class WindowBindingServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreviewCreatesNoLeaseUntilAnExplicitRevalidatedDrop()
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot target = Target();
        FakeWindowSystem windows = new(target);
        await using WindowBindingService service = CreateService(windows, clock);

        await service.BeginDragAsync();
        WindowBindingActionResult preview = await service.PreviewAtAsync(
            new PhysicalScreenPoint(-950, 150));

        Assert.True(preview.IsSuccess);
        Assert.Equal(WindowBindingMode.PreviewEligible, preview.Snapshot.Mode);
        Assert.Null(preview.Snapshot.Lease);
        Assert.Equal(0, windows.WatchCount);

        WindowBindingActionResult dropped = await service.DropAsync(Companion());

        Assert.True(dropped.IsSuccess);
        Assert.Equal(WindowBindingMode.Bound, dropped.Snapshot.Mode);
        Assert.Equal(WindowLeaseState.Active, dropped.Snapshot.Lease!.State);
        Assert.Equal(1, windows.ObserveCount);
        Assert.Equal(1, windows.WatchCount);
        Assert.Equal(
            Enum.GetValues<WindowContextCapability>().Order(),
            dropped.Snapshot.Lease.ContextCapabilities.Order());
    }

    [Fact]
    public async Task BeginningADragRevokesAndUnsubscribesTheExistingLeaseImmediately()
    {
        ManualClock clock = new(Now);
        FakeWindowSystem windows = new(Target());
        await using WindowBindingService service = CreateService(windows, clock);
        await IssueAsync(service);

        WindowBindingActionResult result = await service.BeginDragAsync();

        Assert.Equal(WindowBindingMode.Dragging, result.Snapshot.Mode);
        Assert.Equal(WindowLeaseState.Revoked, result.Snapshot.Lease!.State);
        Assert.Equal(
            WindowLeaseRevocationReason.UserRemovedPet,
            result.Snapshot.Lease.RevocationReason);
        Assert.Equal(1, windows.LastMonitor!.DisposeCount);
    }

    [Fact]
    public async Task DropFailsClosedWhenTheHwndWasReusedBeforeIssue()
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot initial = Target();
        FakeWindowSystem windows = new(initial)
        {
            Observation = WindowTargetObservation.Valid(Target(
                processId: initial.Identity.ProcessId + 1,
                processStartedAt: Now.AddMinutes(-1))),
        };
        await using WindowBindingService service = CreateService(windows, clock);
        await service.BeginDragAsync();
        await service.PreviewAtAsync(new PhysicalScreenPoint(-950, 150));

        WindowBindingActionResult result = await service.DropAsync(Companion());

        Assert.False(result.IsSuccess);
        Assert.Equal("window_target.identity_changed", result.ReasonCode);
        Assert.Equal(WindowBindingMode.PreviewIneligible, result.Snapshot.Mode);
        Assert.Null(result.Snapshot.Lease);
        Assert.Equal(0, windows.WatchCount);
    }

    [Fact]
    public async Task LateDropRevalidationCannotReactivateACancelledDrag()
    {
        ManualClock clock = new(Now);
        FakeWindowSystem windows = new(Target())
        {
            ObserveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AllowObserve = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using WindowBindingService service = CreateService(windows, clock);
        await service.BeginDragAsync();
        await service.PreviewAtAsync(new PhysicalScreenPoint(-950, 150));

        Task<WindowBindingActionResult> pendingDrop =
            service.DropAsync(Companion()).AsTask();
        await windows.ObserveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        WindowBindingActionResult cancelled = await service.CancelDragAsync();
        windows.AllowObserve.SetResult();
        WindowBindingActionResult staleDrop = await pendingDrop;

        Assert.Equal(WindowBindingMode.Home, cancelled.Snapshot.Mode);
        Assert.False(staleDrop.IsSuccess);
        Assert.Equal("window_binding.stale_drop", staleDrop.ReasonCode);
        Assert.Equal(WindowBindingMode.Home, service.Current.Mode);
        Assert.False(service.Current.HasActiveLease);
        Assert.Equal(1, windows.LastMonitor!.DisposeCount);
    }

    [Fact]
    public async Task ReconciliationRevokesWhenProcessIdentityChangesAfterIssue()
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot initial = Target();
        FakeWindowSystem windows = new(initial);
        await using WindowBindingService service = CreateService(windows, clock);
        await IssueAsync(service);
        windows.Observation = WindowTargetObservation.Valid(Target(
            processId: initial.Identity.ProcessId + 1,
            processStartedAt: Now.AddMinutes(-1)));

        await windows.LastMonitor!.SignalAsync(new WindowTargetSignal(
            WindowTargetSignalKind.LocationChanged,
            initial.Identity.RootWindowHandle));
        WindowBindingSnapshot revoked = await WaitForAsync(
            service,
            static state => state.Lease?.State == WindowLeaseState.Revoked);

        Assert.Equal(WindowBindingMode.Revoked, revoked.Mode);
        Assert.Equal(
            WindowLeaseRevocationReason.TargetIdentityChanged,
            revoked.Lease!.RevocationReason);
        Assert.Equal("window_target.identity_changed", revoked.ReasonCode);
    }

    [Theory]
    [InlineData(WindowTargetSignalKind.Destroyed, WindowLeaseRevocationReason.TargetDestroyed)]
    [InlineData(WindowTargetSignalKind.ProcessExited, WindowLeaseRevocationReason.TargetDestroyed)]
    [InlineData(WindowTargetSignalKind.Cloaked, WindowLeaseRevocationReason.TargetIneligible)]
    public async Task TerminalNativeSignalsRevokeWithoutTransferringTheLease(
        WindowTargetSignalKind signalKind,
        WindowLeaseRevocationReason expectedReason)
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot target = Target();
        FakeWindowSystem windows = new(target);
        await using WindowBindingService service = CreateService(windows, clock);
        await IssueAsync(service);
        int observationsBeforeSignal = windows.ObserveCount;

        await windows.LastMonitor!.SignalAsync(new WindowTargetSignal(
            signalKind,
            target.Identity.RootWindowHandle));
        WindowBindingSnapshot revoked = await WaitForAsync(
            service,
            static state => state.Lease?.State == WindowLeaseState.Revoked);

        Assert.Equal(expectedReason, revoked.Lease!.RevocationReason);
        Assert.Equal(observationsBeforeSignal, windows.ObserveCount);
    }

    [Fact]
    public async Task ReconciliationExpiresAnElapsedLease()
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot target = Target();
        FakeWindowSystem windows = new(target);
        await using WindowBindingService service = CreateService(windows, clock);
        await IssueAsync(service);
        clock.UtcNow = Now.AddMinutes(31);

        await windows.LastMonitor!.SignalAsync(new WindowTargetSignal(
            WindowTargetSignalKind.ReconcileRequested,
            target.Identity.RootWindowHandle));
        WindowBindingSnapshot expired = await WaitForAsync(
            service,
            static state => state.Lease?.State == WindowLeaseState.Expired);

        Assert.Equal(WindowBindingMode.Expired, expired.Mode);
        Assert.Equal(WindowLeaseRevocationReason.Expired, expired.Lease!.RevocationReason);
        Assert.Equal("window_lease.expired", expired.ReasonCode);
    }

    [Fact]
    public async Task MinimizeAndRestoreRemainContextOnlyAndUpdatePresentation()
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot target = Target();
        FakeWindowSystem windows = new(target);
        await using WindowBindingService service = CreateService(windows, clock);
        await IssueAsync(service);
        windows.Observation = WindowTargetObservation.Valid(Target(isMinimized: true));

        await windows.LastMonitor!.SignalAsync(new WindowTargetSignal(
            WindowTargetSignalKind.MinimizeStarted,
            target.Identity.RootWindowHandle));
        WindowBindingSnapshot minimized = await WaitForAsync(
            service,
            static state => state.Mode == WindowBindingMode.TargetMinimized);

        windows.Observation = WindowTargetObservation.Valid(Target());
        await windows.LastMonitor.SignalAsync(new WindowTargetSignal(
            WindowTargetSignalKind.MinimizeEnded,
            target.Identity.RootWindowHandle));
        WindowBindingSnapshot restored = await WaitForAsync(
            service,
            state => state.Mode == WindowBindingMode.Bound &&
                state.Revision > minimized.Revision);

        Assert.Equal(WindowLeaseState.Active, restored.Lease!.State);
        Assert.False(restored.ObservedTarget!.IsMinimized);
    }

    [Fact]
    public async Task KeyboardTargetPickerIsBoundedDeduplicatedAndRevalidated()
    {
        ManualClock clock = new(Now);
        WindowTargetSnapshot target = Target();
        FakeWindowSystem windows = new(target)
        {
            EnumeratedTargets = [target, target, Target(windowHandle: 0x30)],
        };
        await using WindowBindingService service = CreateService(windows, clock);

        IReadOnlyList<WindowTargetSnapshot> choices =
            await service.EnumerateKeyboardTargetsAsync();
        WindowBindingActionResult attached = await service.AttachFromKeyboardAsync(
            Companion(),
            choices[0]);

        Assert.Equal(2, choices.Count);
        Assert.True(attached.IsSuccess);
        Assert.Equal(WindowBindingMode.Bound, attached.Snapshot.Mode);
        Assert.Equal(1, windows.ObserveCount);
    }

    [Fact]
    public async Task UnexpectedMonitorCompletionFailsClosed()
    {
        ManualClock clock = new(Now);
        FakeWindowSystem windows = new(Target());
        await using WindowBindingService service = CreateService(windows, clock);
        await IssueAsync(service);

        windows.LastMonitor!.Complete();
        WindowBindingSnapshot revoked = await WaitForAsync(
            service,
            static state => state.ReasonCode == "window_target.monitor_disconnected");

        Assert.Equal(WindowLeaseState.Revoked, revoked.Lease!.State);
        Assert.Equal(
            WindowLeaseRevocationReason.TargetIneligible,
            revoked.Lease.RevocationReason);
    }

    [Fact]
    public void ContractProjectionContainsOnlyContextCapabilitiesAndOmitsNullOptionals()
    {
        WindowLease lease = WindowLease.Issue(
            new LeaseId(Guid.Parse("55555555-5555-4555-8555-555555555555")),
            new CompanionId(Guid.Parse("66666666-6666-4666-8666-666666666666")),
            Target().Identity,
            Now,
            Now.AddMinutes(30));

        string json = Encoding.UTF8.GetString(
            ContractJson.Serialize(WindowLeaseContractMapper.ToContract(lease)));

        Assert.Contains("\"state\":\"active\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hwnd\":\"0x10\"", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"contextCapabilities\":[\"anchor_body\",\"present_run_status\",\"identify_target_for_user\"]",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("revocation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(ContractJson.ParseWindowLease(Encoding.UTF8.GetBytes(json)).IsSuccess);
    }

    private static WindowBindingService CreateService(
        FakeWindowSystem windows,
        ManualClock clock) =>
        new(
            windows,
            clock,
            new SequentialIdGenerator(),
            new WindowBindingPolicy(TimeSpan.FromMinutes(30), 8));

    private static async Task IssueAsync(WindowBindingService service)
    {
        await service.BeginDragAsync();
        await service.PreviewAtAsync(new PhysicalScreenPoint(-950, 150));
        WindowBindingActionResult result = await service.DropAsync(Companion());
        Assert.True(result.IsSuccess, result.ReasonCode);
    }

    private static CompanionId Companion() =>
        new(Guid.Parse("66666666-6666-4666-8666-666666666666"));

    private static WindowTargetSnapshot Target(
        ulong windowHandle = 0x10,
        int processId = 42,
        DateTimeOffset? processStartedAt = null,
        bool isMinimized = false) =>
        new(
            new WindowTargetIdentity(
                windowHandle,
                windowHandle,
                processId,
                processStartedAt ?? Now.AddMinutes(-5),
                "Synthetic target",
                "Display-only title"),
            new PhysicalScreenRectangle(-1000, 100, -500, 700),
            isMinimized,
            isForeground: true);

    private static async Task<WindowBindingSnapshot> WaitForAsync(
        WindowBindingService service,
        Func<WindowBindingSnapshot, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!predicate(service.Current))
        {
            await Task.Delay(10, timeout.Token);
        }

        return service.Current;
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class SequentialIdGenerator : IIdGenerator
    {
        private int next;

        public Guid NewId()
        {
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes, Interlocked.Increment(ref next));
            return new Guid(bytes);
        }
    }

    private sealed class FakeWindowSystem(WindowTargetSnapshot target) : IWindowSystem
    {
        public WindowDiscoveryResult Discovery { get; set; } =
            WindowDiscoveryResult.Eligible(target);

        public WindowTargetObservation Observation { get; set; } =
            WindowTargetObservation.Valid(target);

        public IReadOnlyList<WindowTargetSnapshot> EnumeratedTargets { get; set; } = [target];

        public int ObserveCount { get; private set; }

        public int WatchCount { get; private set; }

        public FakeWindowTargetMonitor? LastMonitor { get; private set; }

        public TaskCompletionSource? ObserveStarted { get; init; }

        public TaskCompletionSource? AllowObserve { get; init; }

        public ValueTask<WindowDiscoveryResult> DiscoverAtAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Discovery);

        public ValueTask<IReadOnlyList<WindowTargetSnapshot>> EnumerateEligibleTargetsAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(EnumeratedTargets);

        public async ValueTask<WindowTargetObservation> ObserveAsync(
            WindowTargetIdentity expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            ObserveCount++;
            ObserveStarted?.TrySetResult();
            if (AllowObserve is not null)
            {
                await AllowObserve.Task.WaitAsync(cancellationToken);
            }

            return Observation;
        }

        public IWindowTargetMonitor Watch(WindowTargetIdentity targetIdentity)
        {
            WatchCount++;
            LastMonitor = new FakeWindowTargetMonitor();
            return LastMonitor;
        }
    }

    private sealed class FakeWindowTargetMonitor : IWindowTargetMonitor
    {
        private readonly Channel<WindowTargetSignal> signals =
            Channel.CreateUnbounded<WindowTargetSignal>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public IAsyncEnumerable<WindowTargetSignal> ReadAllAsync(
            CancellationToken cancellationToken = default) =>
            signals.Reader.ReadAllAsync(cancellationToken);

        public ValueTask SignalAsync(WindowTargetSignal signal) =>
            signals.Writer.WriteAsync(signal);

        public void Complete() => signals.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            signals.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

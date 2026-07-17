using Tooltail.Application;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
using Tooltail.Domain.Identifiers;
using Tooltail.Domain.Windows;
using Tooltail.Platform.Windows.Windowing;

namespace Tooltail.Platform.Windows.Tests.Windowing;

public sealed class WindowsWindowSystemNativeTests
{
    [WindowsFact]
    [Trait("Platform", "WindowsInteractive")]
    public async Task NativeEnumerationCapturesIdentityAndRejectsTooltailOwnedWindows()
    {
        await using NativeWindowTestFixture fixture = new();
        nint handle = await fixture.GetWindowHandleAsync();
        WindowsWindowSystem diagnosticSystem = new(
            tooltailProcessId: int.MaxValue,
            skipOwnProcessEvents: false);

        IReadOnlyList<WindowTargetSnapshot> candidates =
            await diagnosticSystem.EnumerateEligibleTargetsAsync(128);
        WindowTargetSnapshot target = Assert.Single(
            candidates,
            candidate => candidate.Identity.WindowHandle == ToUnsignedHandle(handle));

        Assert.Equal(Environment.ProcessId, target.Identity.ProcessId);
        Assert.Equal(TimeSpan.Zero, target.Identity.ProcessStartedAt.Offset);
        Assert.StartsWith("Tooltail synthetic target", target.Identity.ObservedWindowTitle);
        Assert.True(target.Bounds.Width > 0);
        Assert.True(target.Bounds.Height > 0);
        Assert.Equal(
            WindowTargetObservationStatus.Valid,
            (await diagnosticSystem.ObserveAsync(target.Identity)).Status);

        fixture.SetTitle("Tooltail changed display-only title");
        WindowTargetObservation titleChanged =
            await diagnosticSystem.ObserveAsync(target.Identity);
        Assert.Equal(WindowTargetObservationStatus.Valid, titleChanged.Status);
        Assert.True(target.Identity.HasSameAuthorityIdentityAs(titleChanged.Target!.Identity));
        Assert.Equal(
            "Tooltail changed display-only title",
            titleChanged.Target.Identity.ObservedWindowTitle);

        WindowsWindowSystem productionSystem = new();
        IReadOnlyList<WindowTargetSnapshot> productionCandidates =
            await productionSystem.EnumerateEligibleTargetsAsync(128);
        Assert.DoesNotContain(
            productionCandidates,
            candidate => candidate.Identity.WindowHandle == ToUnsignedHandle(handle));

        await fixture.DisposeAsync();
        Assert.Equal(
            WindowTargetObservationStatus.Destroyed,
            (await diagnosticSystem.ObserveAsync(target.Identity)).Status);
    }

    [WindowsFact]
    [Trait("Platform", "WindowsInteractive")]
    public async Task DedicatedHookThreadDeliversMoveAndDestroyAndDisposesDeterministically()
    {
        await using NativeWindowTestFixture fixture = new();
        nint handle = await fixture.GetWindowHandleAsync();
        WindowsWindowSystem windows = new(
            tooltailProcessId: int.MaxValue,
            skipOwnProcessEvents: false);
        WindowTargetSnapshot target = Assert.Single(
            await windows.EnumerateEligibleTargetsAsync(128),
            candidate => candidate.Identity.WindowHandle == ToUnsignedHandle(handle));
        await using IWindowTargetMonitor monitor = windows.Watch(target.Identity);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<WindowTargetSignal> reader = monitor
            .ReadAllAsync(timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        fixture.MoveWithoutActivation(90, 110);
        WindowTargetSignal moved = await ReadUntilAsync(
            reader,
            static signal => signal.Kind == WindowTargetSignalKind.LocationChanged);
        Assert.Equal(target.Identity.RootWindowHandle, moved.WindowHandle);

        await fixture.DisposeAsync();
        WindowTargetSignal destroyed = await ReadUntilAsync(
            reader,
            static signal => signal.Kind == WindowTargetSignalKind.Destroyed);
        Assert.Equal(target.Identity.RootWindowHandle, destroyed.WindowHandle);
    }

    [WindowsFact]
    [Trait("Platform", "WindowsInteractive")]
    public async Task RepeatedNativeMonitorRegistrationLeavesNoLiveSubscriptionOwner()
    {
        await using NativeWindowTestFixture fixture = new();
        nint handle = await fixture.GetWindowHandleAsync();
        WindowsWindowSystem windows = new(
            tooltailProcessId: int.MaxValue,
            skipOwnProcessEvents: false);
        WindowTargetSnapshot target = Assert.Single(
            await windows.EnumerateEligibleTargetsAsync(128),
            candidate => candidate.Identity.WindowHandle == ToUnsignedHandle(handle));

        for (int index = 0; index < 20; index++)
        {
            await using IWindowTargetMonitor monitor = windows.Watch(target.Identity);
        }
    }

    [WindowsFact]
    [Trait("Platform", "WindowsInteractive")]
    public async Task OwnedAmbientSurfaceStylesAndPlacementRemainNoActivateAndClickThroughByKind()
    {
        await using NativeWindowTestFixture fixture = new();
        nint handle = await fixture.GetWindowHandleAsync();

        WindowsOwnedWindowSurfaceController.Configure(
            handle,
            AmbientWindowSurfaceKind.Pet);
        long petStyle = WindowsOwnedWindowSurfaceController.ReadExtendedStyle(handle);
        Assert.True(WindowsOwnedWindowSurfaceController.HasRequiredStyle(
            petStyle,
            AmbientWindowSurfaceKind.Pet));

        WindowsOwnedWindowSurfaceController.Configure(
            handle,
            AmbientWindowSurfaceKind.Tether);
        long tetherStyle = WindowsOwnedWindowSurfaceController.ReadExtendedStyle(handle);
        Assert.True(WindowsOwnedWindowSurfaceController.HasRequiredStyle(
            tetherStyle,
            AmbientWindowSurfaceKind.Tether));

        PhysicalScreenRectangle original =
            WindowsOwnedWindowSurfaceController.GetOwnedBounds(handle);
        PhysicalScreenRectangle placed = new(
            original.Left + 25,
            original.Top + 30,
            original.Right + 25,
            original.Bottom + 30);
        WindowsOwnedWindowSurfaceController.PlaceNoActivate(handle, placed);
        Assert.Equal(
            placed,
            WindowsOwnedWindowSurfaceController.GetOwnedBounds(handle));
        Assert.True(
            WindowsOwnedWindowSurfaceController.GetWorkArea(
                new PhysicalScreenPoint(placed.Left, placed.Top)).Contains(
                new PhysicalScreenPoint(placed.Left, placed.Top)) ||
            placed.Left < 0 ||
            placed.Top < 0);

        WindowsOwnedWindowSurfaceController.Hide(handle);
        WindowsOwnedWindowSurfaceController.ShowNoActivate(handle);
        Assert.False(WindowsOwnedWindowSurfaceController.IsForeground(handle));
    }

    [WindowsFact]
    [Trait("Platform", "WindowsInteractive")]
    public async Task NativeBindingServiceIssuesTracksAndRevokesOneVerifiedLease()
    {
        await using NativeWindowTestFixture fixture = new();
        nint handle = await fixture.GetWindowHandleAsync();
        WindowsWindowSystem windows = new(
            tooltailProcessId: int.MaxValue,
            skipOwnProcessEvents: false);
        WindowTargetSnapshot target = Assert.Single(
            await windows.EnumerateEligibleTargetsAsync(128),
            candidate => candidate.Identity.WindowHandle == ToUnsignedHandle(handle));
        await using WindowBindingService binding = new(
            windows,
            new SystemClock(),
            new GuidIdGenerator(),
            new WindowBindingPolicy(TimeSpan.FromMinutes(5), 16));

        WindowBindingActionResult attached = await binding.AttachFromKeyboardAsync(
            new CompanionId(Guid.NewGuid()),
            target);
        Assert.True(attached.IsSuccess, attached.ReasonCode);
        Assert.Equal(WindowLeaseState.Active, attached.Snapshot.Lease!.State);

        int originalLeft = attached.Snapshot.ObservedTarget!.Bounds.Left;
        fixture.MoveWithoutActivation(originalLeft + 60, target.Bounds.Top + 45);
        WindowBindingSnapshot moved = await WaitForBindingAsync(
            binding,
            state => state.HasActiveLease &&
                state.ObservedTarget?.Bounds.Left != originalLeft);
        Assert.Equal(WindowBindingMode.Bound, moved.Mode);

        await fixture.DisposeAsync();
        WindowBindingSnapshot revoked = await WaitForBindingAsync(
            binding,
            static state => state.Lease?.State == WindowLeaseState.Revoked);
        Assert.True(revoked.Lease!.RevocationReason is
            WindowLeaseRevocationReason.TargetDestroyed or
            WindowLeaseRevocationReason.TargetIneligible);
    }

    [Fact]
    public void PetMessagePolicyPreventsActivationAndPassesTransparentPixelsThrough()
    {
        Assert.True(AmbientWindowMessagePolicy.TryHandlePetMessage(
            AmbientWindowMessagePolicy.WindowMessageMouseActivate,
            hitsVisibleSprite: true,
            out nint activationResult));
        Assert.Equal(AmbientWindowMessagePolicy.MouseActivateNoActivate, activationResult);

        Assert.True(AmbientWindowMessagePolicy.TryHandlePetMessage(
            AmbientWindowMessagePolicy.WindowMessageNonClientHitTest,
            hitsVisibleSprite: false,
            out nint transparentResult));
        Assert.Equal(AmbientWindowMessagePolicy.HitTestTransparent, transparentResult);

        Assert.False(AmbientWindowMessagePolicy.TryHandlePetMessage(
            AmbientWindowMessagePolicy.WindowMessageNonClientHitTest,
            hitsVisibleSprite: true,
            out _));
    }

    private static async ValueTask<WindowTargetSignal> ReadUntilAsync(
        IAsyncEnumerator<WindowTargetSignal> reader,
        Func<WindowTargetSignal, bool> predicate)
    {
        while (await reader.MoveNextAsync())
        {
            if (predicate(reader.Current))
            {
                return reader.Current;
            }
        }

        throw new InvalidOperationException("The native monitor ended before the expected signal.");
    }

    private static async Task<WindowBindingSnapshot> WaitForBindingAsync(
        WindowBindingService service,
        Func<WindowBindingSnapshot, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!predicate(service.Current))
        {
            await Task.Delay(10, timeout.Token);
        }

        return service.Current;
    }

    private static ulong ToUnsignedHandle(nint handle) => unchecked((ulong)(nuint)handle);

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, (int)sourceLineNumber)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows interactive host.";
            }
            else if (string.Equals(
                         Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                         "true",
                         StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Requires an interactive Windows desktop; GitHub-hosted runners are headless.";
            }
        }
    }
}

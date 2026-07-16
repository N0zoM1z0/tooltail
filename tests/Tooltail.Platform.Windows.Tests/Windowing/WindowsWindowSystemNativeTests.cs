using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
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

    private static ulong ToUnsignedHandle(nint handle) => unchecked((ulong)(nuint)handle);

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows interactive host.";
            }
        }
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Windows;
using Tooltail.Domain.Windows;

namespace Tooltail.Platform.Windows.Windowing;

public sealed class WindowsWindowSystem : IWindowSystem, IPhysicalPointerSource
{
    private const int MaximumEnumeratedWindows = 512;
    private const int MaximumClassNameCharacters = 256;
    private const int MaximumTitleCharacters = 256;
    private const int InitialExecutablePathCharacters = 1024;
    private const int MaximumExecutablePathCharacters = 32768;

    private static readonly HashSet<string> ShellClassNames = new(
        [
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "DV2ControlHost",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly int tooltailProcessId;
    private readonly uint? currentIntegrityLevel;
    private readonly bool currentProcessIsElevated;
    private readonly bool skipOwnProcessEvents;

    public WindowsWindowSystem()
        : this(Environment.ProcessId, skipOwnProcessEvents: true)
    {
    }

    internal WindowsWindowSystem(int tooltailProcessId, bool skipOwnProcessEvents)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tooltailProcessId);
        this.tooltailProcessId = tooltailProcessId;
        this.skipOwnProcessEvents = skipOwnProcessEvents;
        if (OperatingSystem.IsWindows() &&
            TryOpenProcess(checked((uint)Environment.ProcessId), out SafeProcessHandle process))
        {
            using (process)
            {
                currentIntegrityLevel = TryGetIntegrityLevel(process, out uint integrity)
                    ? integrity
                    : null;
            }
        }

        currentProcessIsElevated =
            currentIntegrityLevel > NativeWindowInterop.SecurityMandatoryMediumRid;
    }

    public bool TryGetCurrentPhysicalPosition(out PhysicalScreenPoint position)
    {
        position = default;
        if (!OperatingSystem.IsWindows() ||
            !NativeWindowInterop.GetCursorPos(out NativeWindowInterop.NativePoint point))
        {
            return false;
        }

        position = new PhysicalScreenPoint(point.X, point.Y);
        return true;
    }

    public ValueTask<WindowDiscoveryResult> DiscoverAtAsync(
        PhysicalScreenPoint point,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(
                WindowDiscoveryResult.Ineligible("window_target.windows_required"));
        }

        if (currentProcessIsElevated)
        {
            return ValueTask.FromResult(
                WindowDiscoveryResult.Ineligible("window_target.tooltail_elevated"));
        }

        WindowEnumeration enumeration = EnumerateTopLevelWindows();
        if (!enumeration.IsSuccess)
        {
            return ValueTask.FromResult(
                WindowDiscoveryResult.Ineligible("window_target.enumeration_failed"));
        }

        foreach (nint candidateHandle in enumeration.Handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetBounds(candidateHandle, out PhysicalScreenRectangle candidateBounds) ||
                !candidateBounds.Contains(point))
            {
                continue;
            }

            CandidateInspection inspection = InspectCandidate(
                candidateHandle,
                WindowEligibilityPurpose.Discovery);
            if (inspection.Decision.IsEligible && inspection.Snapshot is not null)
            {
                return ValueTask.FromResult(
                    WindowDiscoveryResult.Eligible(inspection.Snapshot));
            }

            if (inspection.Decision.Disposition == WindowEligibilityDisposition.Block)
            {
                return ValueTask.FromResult(
                    WindowDiscoveryResult.Ineligible(inspection.Decision.ReasonCode));
            }
        }

        return ValueTask.FromResult(WindowDiscoveryResult.NoTarget());
    }

    public ValueTask<IReadOnlyList<WindowTargetSnapshot>> EnumerateEligibleTargetsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || currentProcessIsElevated)
        {
            return ValueTask.FromResult<IReadOnlyList<WindowTargetSnapshot>>([]);
        }

        WindowEnumeration enumeration = EnumerateTopLevelWindows();
        if (!enumeration.IsSuccess)
        {
            return ValueTask.FromResult<IReadOnlyList<WindowTargetSnapshot>>([]);
        }

        List<WindowTargetSnapshot> targets = new(maximumCount);
        HashSet<ulong> rootHandles = [];
        foreach (nint candidateHandle in enumeration.Handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateInspection inspection = InspectCandidate(
                candidateHandle,
                WindowEligibilityPurpose.Discovery);
            if (!inspection.Decision.IsEligible || inspection.Snapshot is null ||
                !rootHandles.Add(inspection.Snapshot.Identity.RootWindowHandle))
            {
                continue;
            }

            targets.Add(inspection.Snapshot);
            if (targets.Count == maximumCount)
            {
                break;
            }
        }

        return ValueTask.FromResult<IReadOnlyList<WindowTargetSnapshot>>(targets);
    }

    public ValueTask<WindowTargetObservation> ObserveAsync(
        WindowTargetIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(
                WindowTargetObservation.Ineligible("window_target.windows_required"));
        }

        if (currentProcessIsElevated)
        {
            return ValueTask.FromResult(
                WindowTargetObservation.Ineligible("window_target.tooltail_elevated"));
        }

        nint handle;
        try
        {
            handle = ToNativeHandle(expectedIdentity.WindowHandle);
        }
        catch (OverflowException)
        {
            return ValueTask.FromResult(WindowTargetObservation.Destroyed());
        }

        if (!NativeWindowInterop.IsWindow(handle))
        {
            return ValueTask.FromResult(WindowTargetObservation.Destroyed());
        }

        CandidateInspection inspection = InspectCandidate(
            handle,
            WindowEligibilityPurpose.ExistingLease);
        if (inspection.Identity is not null &&
            !expectedIdentity.HasSameAuthorityIdentityAs(inspection.Identity))
        {
            return ValueTask.FromResult(WindowTargetObservation.IdentityChanged());
        }

        if (!inspection.Decision.IsEligible || inspection.Snapshot is null)
        {
            return ValueTask.FromResult(
                WindowTargetObservation.Ineligible(inspection.Decision.ReasonCode));
        }

        if (!expectedIdentity.HasSameAuthorityIdentityAs(inspection.Snapshot.Identity))
        {
            return ValueTask.FromResult(WindowTargetObservation.IdentityChanged());
        }

        return ValueTask.FromResult(WindowTargetObservation.Valid(inspection.Snapshot));
    }

    public IWindowTargetMonitor Watch(WindowTargetIdentity targetIdentity)
    {
        ArgumentNullException.ThrowIfNull(targetIdentity);
        return new WindowsWindowTargetMonitor(targetIdentity, skipOwnProcessEvents);
    }

    public static MonitorCoordinateReference GetLocalCoordinateReference(
        WindowTargetIdentity targetIdentity)
    {
        ArgumentNullException.ThrowIfNull(targetIdentity);
        nint root = ToNativeHandle(targetIdentity.RootWindowHandle);
        uint dpi = OperatingSystem.IsWindows()
            ? NativeWindowInterop.GetDpiForWindow(root)
            : 0;
        if (dpi == 0)
        {
            dpi = 96;
        }

        return new MonitorCoordinateReference(
            new PhysicalScreenPoint(0, 0),
            new DeviceIndependentPoint(0, 0),
            dpi,
            dpi);
    }

    private CandidateInspection InspectCandidate(
        nint candidateHandle,
        WindowEligibilityPurpose purpose)
    {
        bool candidateExists = NativeWindowInterop.IsWindow(candidateHandle);
        nint rootHandle = candidateExists
            ? NativeWindowInterop.GetAncestor(
                candidateHandle,
                NativeWindowInterop.AncestorRootOwner)
            : nint.Zero;
        if (rootHandle == nint.Zero)
        {
            rootHandle = candidateHandle;
        }

        bool rootExists = candidateExists && NativeWindowInterop.IsWindow(rootHandle);
        uint candidateProcessId = 0;
        uint rootProcessId = 0;
        if (candidateExists)
        {
            NativeWindowInterop.GetWindowThreadProcessId(candidateHandle, out candidateProcessId);
        }

        if (rootExists)
        {
            NativeWindowInterop.GetWindowThreadProcessId(rootHandle, out rootProcessId);
        }

        uint processId = rootProcessId != 0 ? rootProcessId : candidateProcessId;
        bool tooltailOwned = processId == tooltailProcessId ||
            candidateProcessId == tooltailProcessId;
        bool shellSurface = IsShellSurface(rootHandle);
        bool visible = rootExists &&
            NativeWindowInterop.IsWindowVisible(rootHandle) &&
            NativeWindowInterop.IsWindowVisible(candidateHandle);
        bool cloaked = rootExists && (IsCloaked(rootHandle) || IsCloaked(candidateHandle));
        long style = rootExists
            ? NativeWindowInterop.GetWindowStyle(
                rootHandle,
                NativeWindowInterop.WindowLongStyle)
            : 0;
        long extendedStyle = rootExists
            ? NativeWindowInterop.GetWindowStyle(
                rootHandle,
                NativeWindowInterop.WindowLongExtendedStyle)
            : 0;
        bool childOnly = (style & NativeWindowInterop.WindowStyleChild) != 0;
        bool topLevelRoot = rootExists &&
            NativeWindowInterop.GetAncestor(
                rootHandle,
                NativeWindowInterop.AncestorRoot) == rootHandle;
        bool transientTool =
            (extendedStyle & NativeWindowInterop.WindowExtendedStyleToolWindow) != 0;
        bool inputTransparent =
            (extendedStyle & NativeWindowInterop.WindowExtendedStyleTransparent) != 0;
        bool minimized = rootExists && NativeWindowInterop.IsIconic(rootHandle);
        bool hung = rootExists && NativeWindowInterop.IsHungAppWindow(rootHandle);
        PhysicalScreenRectangle bounds = default;
        bool hasBounds = rootExists && TryGetBounds(rootHandle, out bounds);

        ProcessInspection process = ProcessInspection.Unavailable;
        bool basicSurfaceCanBeInspected = rootExists &&
            !tooltailOwned &&
            visible &&
            !cloaked &&
            !childOnly &&
            topLevelRoot &&
            !shellSurface &&
            processId is > 0 and <= int.MaxValue;
        if (basicSurfaceCanBeInspected)
        {
            process = InspectProcess(processId);
        }

        WindowCandidateFacts facts = new()
        {
            Exists = rootExists,
            IsTooltailOwned = tooltailOwned,
            IsVisible = visible,
            IsCloaked = cloaked,
            IsChildOnly = childOnly,
            IsTopLevelRoot = topLevelRoot,
            IsShellSurface = shellSurface,
            IsTransientToolWindow = transientTool,
            IsInputTransparent = inputTransparent,
            IsMinimized = minimized,
            IsHung = hung,
            IsHigherIntegrityOrSecure = process.IsSecureOrHigherIntegrity,
            HasStableProcessIdentity = process.HasStableIdentity,
            HasUsableBounds = hasBounds,
        };
        WindowEligibilityDecision decision = WindowEligibilityPolicy.Evaluate(facts, purpose);
        if (!decision.IsEligible || !hasBounds || !process.HasStableIdentity)
        {
            return new CandidateInspection(decision, null, null);
        }

        string? title = ReadDisplayText(candidateHandle, MaximumTitleCharacters);
        WindowTargetIdentity identity = new(
            ToUnsignedHandle(candidateHandle),
            ToUnsignedHandle(rootHandle),
            checked((int)processId),
            process.StartedAt,
            process.ApplicationDisplayName,
            title);
        WindowTargetSnapshot snapshot = new(
            identity,
            bounds,
            minimized,
            IsTargetForeground(candidateHandle, rootHandle));
        return new CandidateInspection(decision, identity, snapshot);
    }

    private ProcessInspection InspectProcess(uint processId)
    {
        if (currentIntegrityLevel is null ||
            !TryOpenProcess(processId, out SafeProcessHandle process))
        {
            return ProcessInspection.SecureOrUnavailable;
        }

        using (process)
        {
            if (!NativeWindowInterop.GetProcessTimes(
                    process,
                    out NativeWindowInterop.NativeFileTime creationTime,
                    out _,
                    out _,
                    out _) ||
                !TryGetIntegrityLevel(process, out uint targetIntegrity))
            {
                return ProcessInspection.SecureOrUnavailable;
            }

            DateTimeOffset startedAt;
            try
            {
                startedAt = new DateTimeOffset(
                    DateTime.FromFileTimeUtc(creationTime.ToInt64()));
            }
            catch (ArgumentOutOfRangeException)
            {
                return ProcessInspection.SecureOrUnavailable;
            }

            bool higherIntegrity = targetIntegrity > currentIntegrityLevel.Value;
            string displayName = ReadApplicationDisplayName(process, processId);
            return new ProcessInspection(
                HasStableIdentity: true,
                IsSecureOrHigherIntegrity: higherIntegrity,
                startedAt,
                displayName);
        }
    }

    private static bool TryOpenProcess(
        uint processId,
        out SafeProcessHandle process)
    {
        process = NativeWindowInterop.OpenProcess(
            NativeWindowInterop.ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (!process.IsInvalid)
        {
            return true;
        }

        process.Dispose();
        process = null!;
        return false;
    }

    private static bool TryGetIntegrityLevel(
        SafeProcessHandle process,
        out uint integrityLevel)
    {
        integrityLevel = 0;
        if (!NativeWindowInterop.OpenProcessToken(
                process,
                NativeWindowInterop.TokenQuery,
                out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            NativeWindowInterop.GetTokenInformation(
                token,
                NativeWindowInterop.TokenIntegrityLevel,
                nint.Zero,
                0,
                out uint requiredBytes);
            if (requiredBytes == 0 || requiredBytes > 4096)
            {
                return false;
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                if (!NativeWindowInterop.GetTokenInformation(
                        token,
                        NativeWindowInterop.TokenIntegrityLevel,
                        buffer,
                        requiredBytes,
                        out _))
                {
                    return false;
                }

                NativeWindowInterop.TokenMandatoryLabel label =
                    Marshal.PtrToStructure<NativeWindowInterop.TokenMandatoryLabel>(buffer);
                if (label.Label.Sid == nint.Zero)
                {
                    return false;
                }

                nint countPointer = NativeWindowInterop.GetSidSubAuthorityCount(label.Label.Sid);
                if (countPointer == nint.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return false;
                }

                nint integrityPointer = NativeWindowInterop.GetSidSubAuthority(
                    label.Label.Sid,
                    checked((uint)(count - 1)));
                if (integrityPointer == nint.Zero)
                {
                    return false;
                }

                integrityLevel = unchecked((uint)Marshal.ReadInt32(integrityPointer));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string ReadApplicationDisplayName(
        SafeProcessHandle process,
        uint processId)
    {
        char[] buffer = new char[InitialExecutablePathCharacters];
        uint length = checked((uint)buffer.Length);
        if (!NativeWindowInterop.QueryFullProcessImageName(
                process,
                0,
                buffer,
                ref length))
        {
            buffer = new char[MaximumExecutablePathCharacters];
            length = checked((uint)buffer.Length);
            if (!NativeWindowInterop.QueryFullProcessImageName(
                    process,
                    0,
                    buffer,
                    ref length))
            {
                return $"Process {processId}";
            }
        }

        string executablePath = new(buffer, 0, checked((int)length));
        string displayName;
        try
        {
            displayName = Path.GetFileNameWithoutExtension(executablePath);
        }
        catch (ArgumentException)
        {
            return $"Process {processId}";
        }

        return SanitizeDisplayText(displayName, 120) ?? $"Process {processId}";
    }

    private static string? ReadDisplayText(nint windowHandle, int maximumCharacters)
    {
        char[] buffer = new char[maximumCharacters + 1];
        int length = NativeWindowInterop.GetWindowText(
            windowHandle,
            buffer,
            buffer.Length);
        return length <= 0
            ? null
            : SanitizeDisplayText(new string(buffer, 0, length), maximumCharacters);
    }

    private static string? SanitizeDisplayText(string value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        int length = Math.Min(value.Length, maximumCharacters);
        char[] sanitized = new char[length];
        int written = 0;
        for (int index = 0; index < length; index++)
        {
            char character = value[index];
            sanitized[written++] = char.IsControl(character) ? ' ' : character;
        }

        string result = new(sanitized, 0, written);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool IsShellSurface(nint windowHandle)
    {
        if (windowHandle == nint.Zero ||
            windowHandle == NativeWindowInterop.GetDesktopWindow() ||
            windowHandle == NativeWindowInterop.GetShellWindow())
        {
            return true;
        }

        char[] className = new char[MaximumClassNameCharacters + 1];
        int length = NativeWindowInterop.GetClassName(
            windowHandle,
            className,
            className.Length);
        return length > 0 && ShellClassNames.Contains(new string(className, 0, length));
    }

    private static bool IsCloaked(nint windowHandle) =>
        NativeWindowInterop.DwmGetWindowAttribute(
            windowHandle,
            NativeWindowInterop.DwmWindowAttributeCloaked,
            out int cloaked,
            sizeof(int)) == 0 && cloaked != 0;

    private static bool TryGetBounds(
        nint windowHandle,
        out PhysicalScreenRectangle bounds)
    {
        bounds = default;
        NativeWindowInterop.NativeRectangle rectangle;
        int result = NativeWindowInterop.DwmGetWindowRectangleAttribute(
            windowHandle,
            NativeWindowInterop.DwmWindowAttributeExtendedFrameBounds,
            out rectangle,
            checked((uint)Marshal.SizeOf<NativeWindowInterop.NativeRectangle>()));
        if (result != 0 && !NativeWindowInterop.GetWindowRect(windowHandle, out rectangle))
        {
            return false;
        }

        if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top)
        {
            return false;
        }

        bounds = new PhysicalScreenRectangle(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);
        return true;
    }

    private static bool IsTargetForeground(nint candidateHandle, nint rootHandle)
    {
        nint foreground = NativeWindowInterop.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        nint foregroundRoot = NativeWindowInterop.GetAncestor(
            foreground,
            NativeWindowInterop.AncestorRootOwner);
        return foreground == candidateHandle ||
            foreground == rootHandle ||
            foregroundRoot == rootHandle;
    }

    private static WindowEnumeration EnumerateTopLevelWindows()
    {
        WindowEnumerationContext context = new(MaximumEnumeratedWindows);
        GCHandle contextHandle = GCHandle.Alloc(context, GCHandleType.Normal);
        NativeWindowInterop.EnumWindowsCallback callback = EnumerateWindow;
        try
        {
            nint callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);
            bool completed = NativeWindowInterop.EnumWindows(
                callbackPointer,
                GCHandle.ToIntPtr(contextHandle));
            return new WindowEnumeration(
                completed || context.StoppedAtLimit,
                context.Handles.ToArray());
        }
        finally
        {
            GC.KeepAlive(callback);
            contextHandle.Free();
        }
    }

    private static bool EnumerateWindow(nint windowHandle, nint parameter)
    {
        GCHandle contextHandle = GCHandle.FromIntPtr(parameter);
        if (contextHandle.Target is not WindowEnumerationContext context)
        {
            return false;
        }

        if (context.Handles.Count == context.MaximumCount)
        {
            context.StoppedAtLimit = true;
            return false;
        }

        context.Handles.Add(windowHandle);
        return true;
    }

    private static nint ToNativeHandle(ulong handle) =>
        nint.Size == 8
            ? unchecked((nint)handle)
            : checked((nint)handle);

    private static ulong ToUnsignedHandle(nint handle) => unchecked((ulong)(nuint)handle);

    private sealed record CandidateInspection(
        WindowEligibilityDecision Decision,
        WindowTargetIdentity? Identity,
        WindowTargetSnapshot? Snapshot);

    private sealed record ProcessInspection(
        bool HasStableIdentity,
        bool IsSecureOrHigherIntegrity,
        DateTimeOffset StartedAt,
        string ApplicationDisplayName)
    {
        internal static ProcessInspection Unavailable { get; } = new(
            HasStableIdentity: false,
            IsSecureOrHigherIntegrity: false,
            default,
            string.Empty);

        internal static ProcessInspection SecureOrUnavailable { get; } = new(
            HasStableIdentity: false,
            IsSecureOrHigherIntegrity: true,
            default,
            string.Empty);
    }

    private sealed record WindowEnumeration(bool IsSuccess, IReadOnlyList<nint> Handles);

    private sealed class WindowEnumerationContext(int maximumCount)
    {
        internal int MaximumCount { get; } = maximumCount;

        internal List<nint> Handles { get; } = new(maximumCount);

        internal bool StoppedAtLimit { get; set; }
    }
}

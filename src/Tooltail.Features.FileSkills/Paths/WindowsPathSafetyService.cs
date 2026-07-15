using Tooltail.Application.Abstractions;
using Tooltail.Domain.Permissions;

namespace Tooltail.Features.FileSkills.Paths;

public sealed class WindowsPathSafetyService
{
    private readonly IFileSystemPathProbe pathProbe;

    public WindowsPathSafetyService(IFileSystemPathProbe pathProbe)
    {
        ArgumentNullException.ThrowIfNull(pathProbe);
        this.pathProbe = pathProbe;
    }

    public PathSafetyResult<CanonicalLocalRoot> CaptureRoot(string? selectedPath)
    {
        PathSafetyResult<string> syntax = WindowsPathPolicy.NormalizeSelectedRoot(selectedPath);
        if (!syntax.IsSuccess)
        {
            return FailureFrom<CanonicalLocalRoot>(syntax.Error!);
        }

        FileSystemPathProbeResult selectedProbe = pathProbe.Probe(syntax.Value!);
        PathSafetyError? probeError = MapProbeFailure(selectedProbe, isRoot: true);
        if (probeError is not null)
        {
            return FailureFrom<CanonicalLocalRoot>(probeError);
        }

        if (selectedProbe.EntryKind != FileSystemEntryKind.Directory)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                PathSafetyReasonCodes.RootNotDirectory,
                "The selected root must be an existing directory.");
        }

        if (!selectedProbe.IsLocalFixedDrive)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                PathSafetyReasonCodes.RootNotFixedDrive,
                "The selected root must be on a local fixed drive.");
        }

        if (selectedProbe.IsReparsePoint)
        {
            return ReparseFailure<CanonicalLocalRoot>();
        }

        PathSafetyResult<string> canonicalSyntax =
            WindowsPathPolicy.NormalizeSelectedRoot(selectedProbe.CanonicalPath);
        if (!canonicalSyntax.IsSuccess)
        {
            return FailureFrom<CanonicalLocalRoot>(canonicalSyntax.Error!);
        }

        string canonicalPath = canonicalSyntax.Value!;
        string volumeIdentity = selectedProbe.VolumeIdentity!;
        foreach (string component in WindowsPathPolicy.EnumerateAbsoluteComponents(canonicalPath))
        {
            FileSystemPathProbeResult componentProbe = pathProbe.Probe(component);
            PathSafetyError? componentError = MapProbeFailure(componentProbe, isRoot: true);
            if (componentError is not null)
            {
                return FailureFrom<CanonicalLocalRoot>(componentError);
            }

            if (componentProbe.IsReparsePoint)
            {
                return ReparseFailure<CanonicalLocalRoot>();
            }

            if (componentProbe.EntryKind != FileSystemEntryKind.Directory ||
                !componentProbe.IsLocalFixedDrive)
            {
                return PathSafetyResult.Failure<CanonicalLocalRoot>(
                    PathSafetyReasonCodes.RootIdentityChanged,
                    "A selected root component no longer resolves to a local directory.");
            }

            if (!string.Equals(componentProbe.VolumeIdentity, volumeIdentity, StringComparison.Ordinal))
            {
                return PathSafetyResult.Failure<CanonicalLocalRoot>(
                    PathSafetyReasonCodes.VolumeChanged,
                    "A root component resolved to a different volume.");
            }

            if (string.Equals(component, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    componentProbe.EntryIdentity,
                    selectedProbe.EntryIdentity,
                    StringComparison.Ordinal))
            {
                return PathSafetyResult.Failure<CanonicalLocalRoot>(
                    PathSafetyReasonCodes.RootIdentityChanged,
                    "The selected root changed while its identity was being captured.");
            }
        }

        ResourceRootIdentity identity = new(
            $"winfs-v1:{volumeIdentity}:{selectedProbe.EntryIdentity}");
        return PathSafetyResult.Success(
            new CanonicalLocalRoot(
                canonicalPath,
                identity,
                volumeIdentity,
                selectedProbe.EntryIdentity!));
    }

    public PathSafetyResult<BoundLocalPath> Bind(
        CanonicalLocalRoot root,
        string? relativePath,
        PathEntryExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(root);

        PathSafetyError? rootError = RevalidateRoot(root);
        if (rootError is not null)
        {
            return FailureFrom<BoundLocalPath>(rootError);
        }

        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(relativePath);
        if (!parsed.IsSuccess)
        {
            return FailureFrom<BoundLocalPath>(parsed.Error!);
        }

        WindowsRelativePath relative = parsed.Value!;
        string fullPath = WindowsPathPolicy.Resolve(root, relative);
        if (!WindowsPathPolicy.IsWithinRoot(root.CanonicalPath, fullPath))
        {
            return PathSafetyResult.Failure<BoundLocalPath>(
                PathSafetyReasonCodes.OutsideRoot,
                "The resolved path is outside the granted root.");
        }

        List<PathComponentBinding> components = [];
        bool ancestorMissing = false;
        foreach ((string componentRelativePath, string componentFullPath) in
                 WindowsPathPolicy.EnumerateComponents(root, relative))
        {
            if (ancestorMissing)
            {
                components.Add(new PathComponentBinding(componentRelativePath, false, null, null));
                continue;
            }

            FileSystemPathProbeResult probe = pathProbe.Probe(componentFullPath);
            if (probe.Status == FileSystemPathProbeStatus.NotFound)
            {
                ancestorMissing = true;
                components.Add(new PathComponentBinding(componentRelativePath, false, null, null));
                continue;
            }

            PathSafetyError? probeError = MapProbeFailure(probe, isRoot: false);
            if (probeError is not null)
            {
                return FailureFrom<BoundLocalPath>(probeError);
            }

            if (probe.IsReparsePoint)
            {
                return ReparseFailure<BoundLocalPath>();
            }

            if (!string.Equals(probe.VolumeIdentity, root.VolumeIdentity, StringComparison.Ordinal))
            {
                return PathSafetyResult.Failure<BoundLocalPath>(
                    PathSafetyReasonCodes.VolumeChanged,
                    "A path component resolved to a different volume.");
            }

            if (!WindowsPathPolicy.IsWithinRoot(root.CanonicalPath, probe.CanonicalPath!))
            {
                return PathSafetyResult.Failure<BoundLocalPath>(
                    PathSafetyReasonCodes.OutsideRoot,
                    "A path component resolved outside the granted root.");
            }

            bool isFinal = string.Equals(componentRelativePath, relative.Value, StringComparison.Ordinal);
            if (!isFinal && probe.EntryKind != FileSystemEntryKind.Directory)
            {
                return PathSafetyResult.Failure<BoundLocalPath>(
                    PathSafetyReasonCodes.ParentNotDirectory,
                    "An existing parent component is not a directory.");
            }

            components.Add(
                new PathComponentBinding(
                    componentRelativePath,
                    existed: true,
                    probe.EntryKind,
                    probe.EntryIdentity));
        }

        bool finalExists = components[^1].Existed;
        if (expectation == PathEntryExpectation.MustExist && !finalExists)
        {
            return PathSafetyResult.Failure<BoundLocalPath>(
                PathSafetyReasonCodes.SourceMissing,
                "The required source path does not exist.");
        }

        if (expectation == PathEntryExpectation.MustNotExist && finalExists)
        {
            return PathSafetyResult.Failure<BoundLocalPath>(
                PathSafetyReasonCodes.DestinationExists,
                "The destination path must be absent.");
        }

        return PathSafetyResult.Success(
            new BoundLocalPath(root, relative, fullPath, expectation, components));
    }

    public PathSafetyResult<CanonicalLocalRoot> CaptureSubroot(
        CanonicalLocalRoot parentRoot,
        string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(parentRoot);
        PathSafetyResult<BoundLocalPath> binding = Bind(
            parentRoot,
            relativePath,
            PathEntryExpectation.MustExist);
        if (!binding.IsSuccess)
        {
            return FailureFrom<CanonicalLocalRoot>(binding.Error!);
        }

        BoundLocalPath value = binding.Value!;
        PathComponentBinding final = value.Components[^1];
        if (final.EntryKind != FileSystemEntryKind.Directory ||
            string.IsNullOrWhiteSpace(final.EntryIdentity))
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                PathSafetyReasonCodes.RootNotDirectory,
                "The owned subroot must be an existing directory.");
        }

        return PathSafetyResult.Success(
            new CanonicalLocalRoot(
                value.FullPath,
                new ResourceRootIdentity(
                    $"winfs-v1:{parentRoot.VolumeIdentity}:{final.EntryIdentity}"),
                parentRoot.VolumeIdentity,
                final.EntryIdentity));
    }

    public PathSafetyResult<BoundLocalPath> Revalidate(BoundLocalPath binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        PathSafetyResult<BoundLocalPath> current =
            Bind(binding.Root, binding.RelativePath.Value, binding.Expectation);
        if (!current.IsSuccess)
        {
            return current;
        }

        BoundLocalPath currentBinding = current.Value!;
        for (int index = 0; index < binding.Components.Count; index++)
        {
            PathComponentBinding expected = binding.Components[index];
            PathComponentBinding actual = currentBinding.Components[index];
            if (expected.Existed != actual.Existed)
            {
                return PathSafetyResult.Failure<BoundLocalPath>(
                    PathSafetyReasonCodes.AssumptionChanged,
                    "Path existence changed after the path was bound.");
            }

            if (expected.Existed &&
                (!string.Equals(expected.EntryIdentity, actual.EntryIdentity, StringComparison.Ordinal) ||
                 expected.EntryKind != actual.EntryKind))
            {
                return PathSafetyResult.Failure<BoundLocalPath>(
                    PathSafetyReasonCodes.IdentityChanged,
                    "A path component identity changed after the path was bound.");
            }
        }

        return current;
    }

    private PathSafetyError? RevalidateRoot(CanonicalLocalRoot root)
    {
        FileSystemPathProbeResult probe = pathProbe.Probe(root.CanonicalPath);
        PathSafetyError? failure = MapProbeFailure(probe, isRoot: true);
        if (failure is not null)
        {
            return failure;
        }

        if (probe.IsReparsePoint ||
            probe.EntryKind != FileSystemEntryKind.Directory ||
            !probe.IsLocalFixedDrive ||
            !PhysicalPathsEqual(probe.CanonicalPath!, root.CanonicalPath) ||
            !string.Equals(probe.VolumeIdentity, root.VolumeIdentity, StringComparison.Ordinal) ||
            !string.Equals(probe.EntryIdentity, root.EntryIdentity, StringComparison.Ordinal))
        {
            return new PathSafetyError(
                PathSafetyReasonCodes.RootIdentityChanged,
                "The granted root identity no longer matches.");
        }

        return null;
    }

    private static bool PhysicalPathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static PathSafetyError? MapProbeFailure(
        FileSystemPathProbeResult probe,
        bool isRoot) =>
        probe.Status switch
        {
            FileSystemPathProbeStatus.Success => null,
            FileSystemPathProbeStatus.NotFound => new PathSafetyError(
                isRoot ? PathSafetyReasonCodes.RootNotFound : PathSafetyReasonCodes.SourceMissing,
                isRoot ? "The selected root does not exist." : "A required path does not exist."),
            FileSystemPathProbeStatus.AccessDenied => new PathSafetyError(
                PathSafetyReasonCodes.AccessDenied,
                "The path could not be inspected with the current user permissions."),
            _ => new PathSafetyError(
                PathSafetyReasonCodes.InspectionFailed,
                "The path could not be inspected safely."),
        };

    private static PathSafetyResult<T> FailureFrom<T>(PathSafetyError error)
        where T : class =>
        PathSafetyResult.Failure<T>(error.Code, error.Message);

    private static PathSafetyResult<T> ReparseFailure<T>()
        where T : class =>
        PathSafetyResult.Failure<T>(
            PathSafetyReasonCodes.ReparsePoint,
            "Reparse points are not supported inside a granted path.");
}

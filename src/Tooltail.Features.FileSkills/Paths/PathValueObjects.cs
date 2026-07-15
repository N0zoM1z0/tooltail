using System.Collections.ObjectModel;
using Tooltail.Application.Abstractions;
using Tooltail.Domain.Permissions;

namespace Tooltail.Features.FileSkills.Paths;

public sealed record WindowsRelativePath
{
    internal WindowsRelativePath(string value) => Value = value;

    public string Value { get; }
}

public sealed record CanonicalLocalRoot
{
    internal CanonicalLocalRoot(
        string canonicalPath,
        ResourceRootIdentity identity,
        string volumeIdentity,
        string entryIdentity)
    {
        CanonicalPath = canonicalPath;
        Identity = identity;
        VolumeIdentity = volumeIdentity;
        EntryIdentity = entryIdentity;
    }

    public string CanonicalPath { get; }

    public ResourceRootIdentity Identity { get; }

    public string VolumeIdentity { get; }

    public string EntryIdentity { get; }
}

public enum PathEntryExpectation
{
    MayExist,
    MustExist,
    MustNotExist,
}

public sealed record PathComponentBinding
{
    public PathComponentBinding(
        string relativePath,
        bool existed,
        FileSystemEntryKind? entryKind,
        string? entryIdentity)
    {
        RelativePath = relativePath;
        Existed = existed;
        EntryKind = entryKind;
        EntryIdentity = entryIdentity;
    }

    public string RelativePath { get; }

    public bool Existed { get; }

    public FileSystemEntryKind? EntryKind { get; }

    public string? EntryIdentity { get; }
}

public sealed record BoundLocalPath
{
    internal BoundLocalPath(
        CanonicalLocalRoot root,
        WindowsRelativePath relativePath,
        string fullPath,
        PathEntryExpectation expectation,
        IEnumerable<PathComponentBinding> components)
    {
        Root = root;
        RelativePath = relativePath;
        FullPath = fullPath;
        Expectation = expectation;
        Components = new ReadOnlyCollection<PathComponentBinding>(components.ToArray());
    }

    public CanonicalLocalRoot Root { get; }

    public WindowsRelativePath RelativePath { get; }

    public string FullPath { get; }

    public PathEntryExpectation Expectation { get; }

    public IReadOnlyList<PathComponentBinding> Components { get; }
}

public sealed record ValidatedPathPair(
    WindowsRelativePath Source,
    WindowsRelativePath Destination);

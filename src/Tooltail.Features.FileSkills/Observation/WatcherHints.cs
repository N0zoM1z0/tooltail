using System.Collections.ObjectModel;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Observation;

public enum WatcherHintKind
{
    Created,
    Deleted,
    Changed,
    Renamed,
}

public sealed record WatcherHint
{
    public WatcherHint(
        WatcherHintKind kind,
        string relativePath,
        string? oldRelativePath = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        RelativePath = RequireNormalizedPath(relativePath, nameof(relativePath));
        if (kind == WatcherHintKind.Renamed)
        {
            OldRelativePath = RequireNormalizedPath(oldRelativePath, nameof(oldRelativePath));
            if (string.Equals(
                    OldRelativePath,
                    RelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "A rename hint must describe two distinct Windows paths.",
                    nameof(oldRelativePath));
            }
        }
        else if (oldRelativePath is not null)
        {
            throw new ArgumentException(
                "Only a rename hint can carry an old path.",
                nameof(oldRelativePath));
        }

        Kind = kind;
    }

    public WatcherHintKind Kind { get; }

    public string RelativePath { get; }

    public string? OldRelativePath { get; }

    private static string RequireNormalizedPath(string? value, string parameterName)
    {
        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(value);
        if (!parsed.IsSuccess ||
            !string.Equals(parsed.Value!.Value, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Watcher hints require a normalized Windows-relative path.",
                parameterName);
        }

        return parsed.Value.Value;
    }
}

public sealed record WatcherHintBatch
{
    public WatcherHintBatch(
        IEnumerable<WatcherHint> hints,
        bool overflowed,
        bool quiesced,
        int droppedHintCount = 0,
        bool sourceFaulted = false)
    {
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentOutOfRangeException.ThrowIfNegative(droppedHintCount);

        WatcherHint[] materialized = hints.ToArray();
        if (materialized.Any(static hint => hint is null))
        {
            throw new ArgumentException("A watcher hint batch cannot contain null values.", nameof(hints));
        }

        Hints = new ReadOnlyCollection<WatcherHint>(materialized);
        Overflowed = overflowed;
        Quiesced = quiesced;
        DroppedHintCount = droppedHintCount;
        SourceFaulted = sourceFaulted;
    }

    public IReadOnlyList<WatcherHint> Hints { get; }

    public bool Overflowed { get; }

    public bool Quiesced { get; }

    public int DroppedHintCount { get; }

    public bool SourceFaulted { get; }

    public static WatcherHintBatch Empty { get; } = new(
        [],
        overflowed: false,
        quiesced: true);
}

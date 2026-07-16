using System.Buffers;
using System.Text;

namespace Tooltail.Features.FileSkills.Paths;

public static class WindowsPathPolicy
{
    public const int MaximumRelativePathLength = 1024;
    public const int MaximumSegmentLength = 255;

    private static readonly SearchValues<char> InvalidCharacters =
        SearchValues.Create("<>\"/|?*");

    private static readonly HashSet<string> ReservedBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "COM¹",
        "COM²",
        "COM³",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
        "LPT¹",
        "LPT²",
        "LPT³",
    };

    public static PathSafetyResult<WindowsRelativePath> ParseRelative(string? input)
    {
        PathSafetyError? wholePathError = ValidateWholeRelativePath(input);
        if (wholePathError is not null)
        {
            return PathSafetyResult.Failure<WindowsRelativePath>(wholePathError.Code, wholePathError.Message);
        }

        foreach (string segment in input!.Split('\\'))
        {
            PathSafetyError? segmentError = ValidateSegment(segment);
            if (segmentError is not null)
            {
                return PathSafetyResult.Failure<WindowsRelativePath>(segmentError.Code, segmentError.Message);
            }
        }

        return PathSafetyResult.Success(new WindowsRelativePath(input));
    }

    public static PathSafetyResult<ValidatedPathPair> ValidateDistinctPair(
        WindowsRelativePath source,
        WindowsRelativePath destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (string.Equals(source.Value, destination.Value, StringComparison.Ordinal))
        {
            return PathSafetyResult.Failure<ValidatedPathPair>(
                PathSafetyReasonCodes.SourceEqualsDestination,
                "The source and destination must be distinct paths.");
        }

        if (string.Equals(source.Value, destination.Value, StringComparison.OrdinalIgnoreCase))
        {
            return PathSafetyResult.Failure<ValidatedPathPair>(
                PathSafetyReasonCodes.CaseOnlyChangeUnsupported,
                "Case-only path changes are not supported in v0.1.");
        }

        return PathSafetyResult.Success(new ValidatedPathPair(source, destination));
    }

    public static bool IsWithinRoot(string canonicalRoot, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        if (UsesPortablePhysicalPath(canonicalRoot))
        {
            string relative = Path.GetRelativePath(canonicalRoot, candidate);
            return relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative);
        }

        string normalizedRoot = TrimTrailingSeparatorUnlessDriveRoot(canonicalRoot);
        if (string.Equals(normalizedRoot, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string boundary = normalizedRoot.EndsWith('\\') ? normalizedRoot : $"{normalizedRoot}\\";
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    internal static PathSafetyResult<string> NormalizeSelectedRoot(string? selectedPath)
    {
        if (!string.IsNullOrEmpty(selectedPath) && HasDevicePrefix(selectedPath))
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.Device,
                "Device paths are not supported.");
        }

        if (!string.IsNullOrEmpty(selectedPath) && selectedPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.Unc,
                "UNC roots are not supported.");
        }

        if (!OperatingSystem.IsWindows() &&
            !string.IsNullOrWhiteSpace(selectedPath) &&
            Path.IsPathFullyQualified(selectedPath))
        {
            return NormalizePortableSelectedRoot(selectedPath);
        }

        if (string.IsNullOrWhiteSpace(selectedPath) ||
            selectedPath.Length < 3 ||
            !IsAsciiLetter(selectedPath[0]) ||
            selectedPath[1] != ':' ||
            selectedPath[2] != '\\')
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.RootNotLocalAbsolute,
                "The selected root must be an absolute local drive path.");
        }

        if (selectedPath.Contains('/'))
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.InvalidSeparator,
                "Windows paths must use a single normalized separator.");
        }

        if (!selectedPath.IsNormalized(NormalizationForm.FormC))
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.NotNormalized,
                "Paths must already be normalized to Unicode NFC.");
        }

        string normalized = TrimTrailingSeparatorUnlessDriveRoot(selectedPath);
        if (normalized.Length > MaximumRelativePathLength)
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.TooLong,
                "The selected root exceeds the bounded path length.");
        }

        string tail = normalized.Length == 3 ? string.Empty : normalized[3..];
        if (tail.Length > 0)
        {
            if (tail.Contains(':'))
            {
                return PathSafetyResult.Failure<string>(
                    PathSafetyReasonCodes.AlternateStream,
                    "Alternate data streams are not supported.");
            }

            foreach (string segment in tail.Split('\\'))
            {
                PathSafetyError? error = ValidateSegment(segment);
                if (error is not null)
                {
                    return PathSafetyResult.Failure<string>(error.Code, error.Message);
                }
            }
        }

        normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        return PathSafetyResult.Success(normalized);
    }

    internal static string Resolve(CanonicalLocalRoot root, WindowsRelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relativePath);

        if (UsesPortablePhysicalPath(root.CanonicalPath))
        {
            return relativePath.Value
                .Split('\\')
                .Aggregate(root.CanonicalPath, Path.Combine);
        }

        string separator = root.CanonicalPath.EndsWith('\\') ? string.Empty : "\\";
        return string.Concat(root.CanonicalPath, separator, relativePath.Value);
    }

    internal static IEnumerable<(string RelativePath, string FullPath)> EnumerateComponents(
        CanonicalLocalRoot root,
        WindowsRelativePath relativePath)
    {
        string relative = string.Empty;
        string full = root.CanonicalPath;
        foreach (string segment in relativePath.Value.Split('\\'))
        {
            relative = relative.Length == 0 ? segment : $"{relative}\\{segment}";
            full = UsesPortablePhysicalPath(root.CanonicalPath)
                ? Path.Combine(full, segment)
                : full.EndsWith('\\')
                    ? $"{full}{segment}"
                    : $"{full}\\{segment}";
            yield return (relative, full);
        }
    }

    internal static IEnumerable<string> EnumerateAbsoluteComponents(string absolutePath)
    {
        if (UsesPortablePhysicalPath(absolutePath))
        {
            string root = Path.GetPathRoot(absolutePath)!;
            string portableCurrent = Path.TrimEndingDirectorySeparator(root);
            if (portableCurrent.Length == 0)
            {
                portableCurrent = root;
            }

            yield return portableCurrent;
            string tail = absolutePath[root.Length..];
            foreach (string segment in tail.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                portableCurrent = Path.Combine(portableCurrent, segment);
                yield return portableCurrent;
            }

            yield break;
        }

        string current = absolutePath[..3];
        yield return current;

        if (absolutePath.Length == 3)
        {
            yield break;
        }

        foreach (string segment in absolutePath[3..].Split('\\'))
        {
            current = current.EndsWith('\\') ? $"{current}{segment}" : $"{current}\\{segment}";
            yield return current;
        }
    }

    private static PathSafetyError? ValidateWholeRelativePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new PathSafetyError(PathSafetyReasonCodes.Empty, "A relative path is required.");
        }

        if (input.Length > MaximumRelativePathLength)
        {
            return new PathSafetyError(PathSafetyReasonCodes.TooLong, "The relative path exceeds its bounded length.");
        }

        if (!input.IsNormalized(NormalizationForm.FormC))
        {
            return new PathSafetyError(PathSafetyReasonCodes.NotNormalized, "Paths must already be normalized to Unicode NFC.");
        }

        if (HasDevicePrefix(input))
        {
            return new PathSafetyError(PathSafetyReasonCodes.Device, "Device paths are not supported.");
        }

        if (input.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return new PathSafetyError(PathSafetyReasonCodes.Unc, "UNC paths are not supported.");
        }

        if (input[0] is '\\' or '/')
        {
            return new PathSafetyError(PathSafetyReasonCodes.Rooted, "Only relative paths are accepted.");
        }

        if (input.Length >= 2 && IsAsciiLetter(input[0]) && input[1] == ':')
        {
            return new PathSafetyError(PathSafetyReasonCodes.DriveRelative, "Drive-qualified paths are not accepted.");
        }

        if (input.Contains(':'))
        {
            return new PathSafetyError(PathSafetyReasonCodes.AlternateStream, "Alternate data streams are not supported.");
        }

        if (input.Contains('/'))
        {
            return new PathSafetyError(PathSafetyReasonCodes.InvalidSeparator, "Windows paths must use backslash separators.");
        }

        return null;
    }

    private static PathSafetyError? ValidateSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return new PathSafetyError(PathSafetyReasonCodes.EmptySegment, "Empty path segments are not accepted.");
        }

        if (segment is "." or "..")
        {
            return new PathSafetyError(PathSafetyReasonCodes.Traversal, "Dot traversal is not accepted.");
        }

        if (segment.Length > MaximumSegmentLength)
        {
            return new PathSafetyError(PathSafetyReasonCodes.SegmentTooLong, "A path segment exceeds its bounded length.");
        }

        if (segment[^1] is '.' or ' ')
        {
            return new PathSafetyError(
                PathSafetyReasonCodes.TrailingDotOrSpace,
                "Path segments cannot end with a dot or space.");
        }

        if (segment.AsSpan().ContainsAny(InvalidCharacters) || segment.Any(char.IsControl))
        {
            return new PathSafetyError(PathSafetyReasonCodes.InvalidCharacter, "A path segment contains an invalid character.");
        }

        string baseName = segment.Split('.', 2)[0];
        if (ReservedBaseNames.Contains(baseName))
        {
            return new PathSafetyError(PathSafetyReasonCodes.ReservedName, "A reserved Windows device name is not accepted.");
        }

        return null;
    }

    private static bool HasDevicePrefix(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
        path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
        path.StartsWith("\\??\\", StringComparison.Ordinal);

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static PathSafetyResult<string> NormalizePortableSelectedRoot(string selectedPath)
    {
        if (!selectedPath.IsNormalized(NormalizationForm.FormC))
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.NotNormalized,
                "Paths must already be normalized to Unicode NFC.");
        }

        string root = Path.GetPathRoot(selectedPath)!;
        string tail = selectedPath[root.Length..].TrimEnd(Path.DirectorySeparatorChar);
        if (tail.Length > 0)
        {
            foreach (string segment in tail.Split(Path.DirectorySeparatorChar))
            {
                if (segment.Contains('\\'))
                {
                    return PathSafetyResult.Failure<string>(
                        PathSafetyReasonCodes.InvalidSeparator,
                        "Portable fixture roots cannot contain Windows separators.");
                }

                if (segment.Contains(':'))
                {
                    return PathSafetyResult.Failure<string>(
                        PathSafetyReasonCodes.AlternateStream,
                        "Alternate data stream syntax is not supported.");
                }

                PathSafetyError? error = ValidateSegment(segment);
                if (error is not null)
                {
                    return PathSafetyResult.Failure<string>(error.Code, error.Message);
                }
            }
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
        if (normalized.Length == 0)
        {
            normalized = root;
        }

        if (normalized.Length > MaximumRelativePathLength)
        {
            return PathSafetyResult.Failure<string>(
                PathSafetyReasonCodes.TooLong,
                "The selected root exceeds the bounded path length.");
        }

        return PathSafetyResult.Success(normalized);
    }

    private static string TrimTrailingSeparatorUnlessDriveRoot(string path) =>
        path.Length > 3 ? path.TrimEnd('\\') : path;

    private static bool UsesPortablePhysicalPath(string root) =>
        Path.DirectorySeparatorChar != '\\' &&
        Path.IsPathFullyQualified(root) &&
        !(root.Length >= 3 && IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\');
}

namespace Tooltail.Features.FileSkills.Paths;

public sealed record PathSafetyError(string Code, string Message);

public readonly record struct PathSafetyResult<T>
    where T : class
{
    internal PathSafetyResult(T? value, PathSafetyError? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public PathSafetyError? Error { get; }
}

public static class PathSafetyResult
{
    public static PathSafetyResult<T> Success<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new PathSafetyResult<T>(value, null, isSuccess: true);
    }

    public static PathSafetyResult<T> Failure<T>(string code, string message)
        where T : class =>
        new(null, new PathSafetyError(code, message), isSuccess: false);
}

public static class PathSafetyReasonCodes
{
    public const string Empty = "path.empty";
    public const string NotNormalized = "path.not_nfc";
    public const string TooLong = "path.too_long";
    public const string SegmentTooLong = "path.segment_too_long";
    public const string Rooted = "path.rooted";
    public const string Unc = "path.unc";
    public const string Device = "path.device";
    public const string DriveRelative = "path.drive_relative";
    public const string AlternateStream = "path.alternate_stream";
    public const string InvalidSeparator = "path.invalid_separator";
    public const string EmptySegment = "path.empty_segment";
    public const string Traversal = "path.traversal";
    public const string TrailingDotOrSpace = "path.trailing_dot_or_space";
    public const string InvalidCharacter = "path.invalid_character";
    public const string ReservedName = "path.reserved_name";
    public const string RootNotLocalAbsolute = "path.root_not_local_absolute";
    public const string RootNotFound = "path.root_not_found";
    public const string RootNotDirectory = "path.root_not_directory";
    public const string RootNotFixedDrive = "path.root_not_fixed_drive";
    public const string RootIdentityChanged = "path.root_identity_changed";
    public const string ReparsePoint = "path.reparse_point";
    public const string OutsideRoot = "path.outside_root";
    public const string AccessDenied = "path.access_denied";
    public const string InspectionFailed = "path.inspection_failed";
    public const string SourceMissing = "path.source_missing";
    public const string DestinationExists = "path.destination_exists";
    public const string ParentNotDirectory = "path.parent_not_directory";
    public const string VolumeChanged = "path.volume_changed";
    public const string IdentityChanged = "path.identity_changed";
    public const string AssumptionChanged = "path.assumption_changed";
    public const string SourceEqualsDestination = "path.source_equals_destination";
    public const string CaseOnlyChangeUnsupported = "path.case_only_change_unsupported";
}

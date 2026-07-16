using System.Text;
using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Domain.Common;

namespace Tooltail.Adapters.AgentEvents.Codex;

public sealed class CodexExecConfiguration
{
    public const int MaximumPromptUtf8Bytes = 16 * 1024;
    public const int MinimumStandardErrorBytes = 1024;
    public const int MaximumStandardErrorByteLimit = 1024 * 1024;
    public static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(30);

    private CodexExecConfiguration(
        string executablePath,
        string workingDirectory,
        string prompt,
        TimeSpan timeout,
        int maximumStandardErrorBytes,
        AgentEventStreamLimits streamLimits)
    {
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        Prompt = prompt;
        Timeout = timeout;
        MaximumStandardErrorBytes = maximumStandardErrorBytes;
        StreamLimits = streamLimits;
    }

    public string ExecutablePath { get; }

    public string WorkingDirectory { get; }

    public TimeSpan Timeout { get; }

    public int MaximumStandardErrorBytes { get; }

    public AgentEventStreamLimits StreamLimits { get; }

    internal string Prompt { get; }

    public static DomainResult<CodexExecConfiguration> Create(
        string executablePath,
        string workingDirectory,
        string prompt,
        TimeSpan? timeout = null,
        int maximumStandardErrorBytes = 64 * 1024,
        AgentEventStreamLimits? streamLimits = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath) ||
            executablePath.Any(char.IsControl))
        {
            return Failure(
                "codex_exec.executable_path_invalid",
                "The configured Codex executable path must be absolute and control-free.");
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory) ||
            workingDirectory.Any(char.IsControl))
        {
            return Failure(
                "codex_exec.working_directory_invalid",
                "The configured Codex working directory must be absolute and control-free.");
        }

        if (string.IsNullOrWhiteSpace(prompt) ||
            prompt.Contains('\0', StringComparison.Ordinal) ||
            Encoding.UTF8.GetByteCount(prompt) > MaximumPromptUtf8Bytes)
        {
            return Failure(
                "codex_exec.prompt_invalid",
                "The approved prompt is empty, contains a null character, or exceeds its UTF-8 bound.");
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMinutes(10);
        if (effectiveTimeout < MinimumTimeout || effectiveTimeout > MaximumTimeout)
        {
            return Failure(
                "codex_exec.timeout_invalid",
                "The Codex process timeout is outside its closed range.");
        }

        if (maximumStandardErrorBytes is < MinimumStandardErrorBytes or
            > MaximumStandardErrorByteLimit)
        {
            return Failure(
                "codex_exec.stderr_limit_invalid",
                "The stderr discard bound is outside its closed range.");
        }

        return DomainResult.Success(
            new CodexExecConfiguration(
                Path.GetFullPath(executablePath),
                Path.GetFullPath(workingDirectory),
                prompt,
                effectiveTimeout,
                maximumStandardErrorBytes,
                streamLimits ?? AgentEventStreamLimits.Default));
    }

    private static DomainResult<CodexExecConfiguration> Failure(
        string code,
        string message) =>
        DomainResult.Failure<CodexExecConfiguration>(code, message);
}

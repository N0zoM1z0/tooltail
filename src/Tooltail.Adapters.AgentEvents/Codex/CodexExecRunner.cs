using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Codex;

public sealed class CodexExecRunner
{
    private static readonly TimeSpan ChildExitGracePeriod = TimeSpan.FromSeconds(5);
    private readonly ICodexProcessLauncher launcher;

    public CodexExecRunner()
        : this(new SystemCodexProcessLauncher())
    {
    }

    internal CodexExecRunner(ICodexProcessLauncher launcher)
    {
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async Task<CodexExecRunResult> RunAsync(
        CodexExecConfiguration configuration,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Run identity cannot be empty.", nameof(runId));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await CreateWithoutProcessAsync(
                CodexExecRunStatus.Cancelled,
                "codex_exec.cancelled_before_launch",
                runId,
                cancelled: true).ConfigureAwait(false);
        }

        if (!File.Exists(configuration.ExecutablePath))
        {
            return await CreateWithoutProcessAsync(
                CodexExecRunStatus.LaunchFailed,
                "codex_exec.executable_not_found",
                runId).ConfigureAwait(false);
        }

        if (!Directory.Exists(configuration.WorkingDirectory))
        {
            return await CreateWithoutProcessAsync(
                CodexExecRunStatus.LaunchFailed,
                "codex_exec.working_directory_not_found",
                runId).ConfigureAwait(false);
        }

        ICodexChildProcess child;
        try
        {
            child = launcher.Start(CreateStartInfo(configuration));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                IOException or
                UnauthorizedAccessException)
        {
            return await CreateWithoutProcessAsync(
                CodexExecRunStatus.LaunchFailed,
                "codex_exec.launch_failed",
                runId).ConfigureAwait(false);
        }

        await using (child.ConfigureAwait(false))
        {
            Task<CodexEventStreamResult> stdoutTask = CodexJsonlAdapter.ReadAsync(
                child.StandardOutput,
                runId,
                configuration.StreamLimits,
                cancellationToken: cancellationToken);
            Task<BoundedDiscardResult> stderrTask = DiscardBoundedAsync(
                child.StandardError,
                configuration.MaximumStandardErrorBytes);
            Task<PromptWriteStatus> promptTask = WritePromptAsync(
                child.StandardInput,
                configuration.Prompt,
                cancellationToken);
            Task exitTask = child.WaitForExitAsync();
            Task timeoutTask = Task.Delay(configuration.Timeout, CancellationToken.None);
            Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            CodexExecRunStatus? forcedStatus = null;
            string? forcedReason = null;
            bool stdoutHandled = false;
            bool stderrHandled = false;
            bool promptHandled = false;
            CodexEventStreamResult? eventStream = null;
            BoundedDiscardResult? stderr = null;

            while (!exitTask.IsCompleted && forcedStatus is null)
            {
                List<Task> candidates = [exitTask, timeoutTask, cancellationTask];
                if (!stdoutHandled)
                {
                    candidates.Add(stdoutTask);
                }

                if (!stderrHandled)
                {
                    candidates.Add(stderrTask);
                }

                if (!promptHandled)
                {
                    candidates.Add(promptTask);
                }

                Task completed = await Task.WhenAny(candidates).ConfigureAwait(false);
                if (completed == cancellationTask)
                {
                    forcedStatus = CodexExecRunStatus.Cancelled;
                    forcedReason = "codex_exec.cancelled";
                }
                else if (completed == timeoutTask)
                {
                    forcedStatus = CodexExecRunStatus.TimedOut;
                    forcedReason = "codex_exec.timed_out";
                }
                else if (completed == stdoutTask)
                {
                    stdoutHandled = true;
                    eventStream = await stdoutTask.ConfigureAwait(false);
                    if (!eventStream.IsSuccess)
                    {
                        forcedStatus = eventStream.Status ==
                            AgentEventStreamStatus.Cancelled
                            ? CodexExecRunStatus.Cancelled
                            : CodexExecRunStatus.AdapterRejected;
                        forcedReason = forcedStatus == CodexExecRunStatus.Cancelled
                            ? "codex_exec.cancelled"
                            : eventStream.ReasonCode;
                    }
                }
                else if (completed == stderrTask)
                {
                    stderrHandled = true;
                    stderr = await stderrTask.ConfigureAwait(false);
                    if (stderr.Status != BoundedDiscardStatus.Completed)
                    {
                        forcedStatus = CodexExecRunStatus.AdapterRejected;
                        forcedReason = stderr.Status == BoundedDiscardStatus.LimitExceeded
                            ? "codex_exec.stderr_limit"
                            : "codex_exec.stderr_io_failure";
                    }
                }
                else if (completed == promptTask)
                {
                    promptHandled = true;
                    PromptWriteStatus prompt = await promptTask.ConfigureAwait(false);
                    if (prompt != PromptWriteStatus.Completed)
                    {
                        forcedStatus = prompt == PromptWriteStatus.Cancelled
                            ? CodexExecRunStatus.Cancelled
                            : CodexExecRunStatus.AdapterRejected;
                        forcedReason = prompt == PromptWriteStatus.Cancelled
                            ? "codex_exec.cancelled"
                            : "codex_exec.stdin_failure";
                    }
                }
            }

            if (forcedStatus is not null && !exitTask.IsCompleted)
            {
                TryKillOwnedChild(child);
            }

            try
            {
                await exitTask
                    .WaitAsync(ChildExitGracePeriod, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                forcedStatus ??= CodexExecRunStatus.ProcessFailed;
                forcedReason ??= "codex_exec.child_exit_timeout";
            }

            if (eventStream is null)
            {
                try
                {
                    eventStream = await stdoutTask
                        .WaitAsync(ChildExitGracePeriod, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is TimeoutException or IOException or ObjectDisposedException)
                {
                    eventStream = await CreateDisconnectedStreamAsync(runId).ConfigureAwait(false);
                    forcedStatus ??= CodexExecRunStatus.AdapterRejected;
                    forcedReason ??= "codex_exec.stdout_failure";
                }
            }

            if (stderr is null)
            {
                try
                {
                    stderr = await stderrTask
                        .WaitAsync(ChildExitGracePeriod, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is TimeoutException or IOException or ObjectDisposedException)
                {
                    stderr = new BoundedDiscardResult(
                        BoundedDiscardStatus.IoFailure,
                        ByteCount: 0);
                    forcedStatus ??= CodexExecRunStatus.AdapterRejected;
                    forcedReason ??= "codex_exec.stderr_io_failure";
                }
            }

            if (stderr.Status != BoundedDiscardStatus.Completed)
            {
                forcedStatus ??= CodexExecRunStatus.AdapterRejected;
                forcedReason ??= stderr.Status == BoundedDiscardStatus.LimitExceeded
                    ? "codex_exec.stderr_limit"
                    : "codex_exec.stderr_io_failure";
            }

            if (!promptHandled)
            {
                try
                {
                    PromptWriteStatus prompt = await promptTask
                        .WaitAsync(ChildExitGracePeriod, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (prompt != PromptWriteStatus.Completed)
                    {
                        forcedStatus ??= prompt == PromptWriteStatus.Cancelled
                            ? CodexExecRunStatus.Cancelled
                            : CodexExecRunStatus.AdapterRejected;
                        forcedReason ??= prompt == PromptWriteStatus.Cancelled
                            ? "codex_exec.cancelled"
                            : "codex_exec.stdin_failure";
                    }
                }
                catch (TimeoutException)
                {
                    forcedStatus ??= CodexExecRunStatus.AdapterRejected;
                    forcedReason ??= "codex_exec.stdin_failure";
                }
            }

            int? exitCode = exitTask.IsCompletedSuccessfully ? child.ExitCode : null;
            CodexExecRunStatus status;
            string reasonCode;
            if (forcedStatus is not null)
            {
                status = forcedStatus.Value;
                reasonCode = forcedReason!;
            }
            else if (exitCode != 0)
            {
                status = CodexExecRunStatus.ProcessFailed;
                reasonCode = "codex_exec.nonzero_exit";
            }
            else if (!eventStream.IsSuccess)
            {
                status = CodexExecRunStatus.AdapterRejected;
                reasonCode = eventStream.ReasonCode;
            }
            else
            {
                status = CodexExecRunStatus.Completed;
                reasonCode = "codex_exec.complete";
            }

            return new CodexExecRunResult(
                status,
                reasonCode,
                runId,
                exitCode,
                stderr.ByteCount,
                stderr.Status == BoundedDiscardStatus.LimitExceeded,
                eventStream);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(CodexExecConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var startInfo = new ProcessStartInfo
        {
            FileName = configuration.ExecutablePath,
            WorkingDirectory = configuration.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("--ignore-user-config");
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(configuration.WorkingDirectory);
        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    private static async Task<PromptWriteStatus> WritePromptAsync(
        Stream input,
        string prompt,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(prompt);
        try
        {
            await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            await input.DisposeAsync().ConfigureAwait(false);
            return PromptWriteStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            return PromptWriteStatus.Cancelled;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return PromptWriteStatus.IoFailure;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<BoundedDiscardResult> DiscardBoundedAsync(
        Stream input,
        int maximumBytes)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        long byteCount = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, 4096)).ConfigureAwait(false);
                if (read == 0)
                {
                    return new BoundedDiscardResult(
                        BoundedDiscardStatus.Completed,
                        byteCount);
                }

                byteCount = Math.Min(maximumBytes + 1L, byteCount + read);
                if (byteCount > maximumBytes)
                {
                    return new BoundedDiscardResult(
                        BoundedDiscardStatus.LimitExceeded,
                        byteCount);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return new BoundedDiscardResult(BoundedDiscardStatus.IoFailure, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void TryKillOwnedChild(ICodexChildProcess child)
    {
        try
        {
            child.KillTree();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
        }
    }

    private static async Task<CodexExecRunResult> CreateWithoutProcessAsync(
        CodexExecRunStatus status,
        string reasonCode,
        RunId runId,
        bool cancelled = false)
    {
        CodexEventStreamResult eventStream;
        if (cancelled)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            eventStream = await CodexJsonlAdapter.ReadAsync(
                Stream.Null,
                runId,
                cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        else
        {
            eventStream = await CreateDisconnectedStreamAsync(runId).ConfigureAwait(false);
        }

        return new CodexExecRunResult(
            status,
            reasonCode,
            runId,
            ExitCode: null,
            StandardErrorByteCount: 0,
            StandardErrorLimitExceeded: false,
            eventStream);
    }

    private static Task<CodexEventStreamResult> CreateDisconnectedStreamAsync(RunId runId) =>
        CodexJsonlAdapter.ReadAsync(Stream.Null, runId);
}

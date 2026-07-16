using System.Diagnostics;
using System.Text;
using Tooltail.Adapters.AgentEvents.Codex;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Tests.Codex;

public sealed class CodexExecRunnerTests
{
    private static readonly RunId RunId =
        new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));

    [Fact]
    public void CommandUsesArgumentListReadOnlyEphemeralModeAndStdinPrompt()
    {
        CodexExecConfiguration configuration = Configuration(prompt: "private approved prompt");

        ProcessStartInfo startInfo = CodexExecRunner.CreateStartInfo(configuration);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            [
                "exec",
                "--json",
                "--ephemeral",
                "--sandbox",
                "read-only",
                "--ignore-user-config",
                "--cd",
                configuration.WorkingDirectory,
                "-",
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            static argument => argument.Contains("private approved prompt", StringComparison.Ordinal));
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            static argument => argument.Contains("danger", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            static argument => argument.Contains("approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigurationRejectsUnboundedOrImplicitInputs()
    {
        var relativeExecutable = CodexExecConfiguration.Create(
            "codex",
            Environment.CurrentDirectory,
            "prompt");
        var relativeDirectory = CodexExecConfiguration.Create(
            ExistingExecutablePath(),
            "relative",
            "prompt");
        var oversizedPrompt = CodexExecConfiguration.Create(
            ExistingExecutablePath(),
            Environment.CurrentDirectory,
            new string('x', CodexExecConfiguration.MaximumPromptUtf8Bytes + 1));
        var timeout = CodexExecConfiguration.Create(
            ExistingExecutablePath(),
            Environment.CurrentDirectory,
            "prompt",
            TimeSpan.FromHours(1));

        Assert.Equal("codex_exec.executable_path_invalid", relativeExecutable.Error?.Code);
        Assert.Equal("codex_exec.working_directory_invalid", relativeDirectory.Error?.Code);
        Assert.Equal("codex_exec.prompt_invalid", oversizedPrompt.Error?.Code);
        Assert.Equal("codex_exec.timeout_invalid", timeout.Error?.Code);
    }

    [Fact]
    public async Task SuccessfulFakeProcessReceivesPromptOnlyOnStdin()
    {
        var child = FakeChild.Exited(
            Jsonl(
                "{\"type\":\"thread.started\"}",
                "{\"type\":\"turn.completed\"}"),
            stderr: [],
            exitCode: 0);
        var launcher = new FakeLauncher(child);
        var runner = new CodexExecRunner(launcher);

        CodexExecRunResult result = await runner.RunAsync(
            Configuration(prompt: "approved prompt through stdin"),
            RunId);

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CompanionBodyState.CompletedReceipt, result.EventStream.FinalProjection.BodyState);
        Assert.Equal(
            "approved prompt through stdin",
            Encoding.UTF8.GetString(child.WrittenStandardInput));
        Assert.NotNull(launcher.StartInfo);
        Assert.DoesNotContain(
            launcher.StartInfo.ArgumentList,
            static argument => argument.Contains("approved prompt", StringComparison.Ordinal));
        Assert.False(child.Killed);
    }

    [Fact]
    public async Task NonzeroExitRetainsFailedProjectionWithoutStderrContent()
    {
        byte[] secretStderr = Encoding.UTF8.GetBytes("provider secret details");
        var child = FakeChild.Exited(
            Jsonl(
                "{\"type\":\"thread.started\"}",
                "{\"type\":\"turn.failed\",\"error\":{\"message\":\"secret\"}}"),
            secretStderr,
            exitCode: 7);
        var runner = new CodexExecRunner(new FakeLauncher(child));

        CodexExecRunResult result = await runner.RunAsync(Configuration(), RunId);

        Assert.Equal(CodexExecRunStatus.ProcessFailed, result.Status);
        Assert.Equal("codex_exec.nonzero_exit", result.ReasonCode);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(secretStderr.Length, result.StandardErrorByteCount);
        Assert.Equal(CompanionBodyState.Failed, result.EventStream.FinalProjection.BodyState);
        string serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("provider secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserCancellationKillsOnlyOwnedChildAndProjectsCancellation()
    {
        var output = new PrefixThenBlockingStream(
            Jsonl("{\"type\":\"thread.started\"}"));
        var child = FakeChild.Running(output, stderr: []);
        var runner = new CodexExecRunner(new FakeLauncher(child));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        CodexExecRunResult result = await runner.RunAsync(
            Configuration(timeout: TimeSpan.FromSeconds(5)),
            RunId,
            cancellation.Token);

        Assert.Equal(CodexExecRunStatus.Cancelled, result.Status);
        Assert.Equal("codex_exec.cancelled", result.ReasonCode);
        Assert.True(child.Killed);
        Assert.Equal(1, child.KillCount);
        Assert.Equal(
            CompanionBodyState.PausedOrCancelled,
            result.EventStream.FinalProjection.BodyState);
        Assert.Equal("body.cancelled", result.EventStream.FinalProjection.Body.ReasonCode);
    }

    [Fact]
    public async Task TimeoutKillsOwnedChildAndProjectsDisconnect()
    {
        var output = new PrefixThenBlockingStream(
            Jsonl("{\"type\":\"thread.started\"}"));
        var child = FakeChild.Running(output, stderr: []);
        var runner = new CodexExecRunner(new FakeLauncher(child));

        CodexExecRunResult result = await runner.RunAsync(
            Configuration(timeout: TimeSpan.FromSeconds(1)),
            RunId);

        Assert.Equal(CodexExecRunStatus.TimedOut, result.Status);
        Assert.Equal("codex_exec.timed_out", result.ReasonCode);
        Assert.True(child.Killed);
        Assert.Equal(1, child.KillCount);
        Assert.Equal(CompanionBodyState.Disconnected, result.EventStream.FinalProjection.BodyState);
    }

    [Fact]
    public async Task StderrLimitKillsOwnedChildWithoutRetainingBytes()
    {
        var output = new PrefixThenBlockingStream(
            Jsonl("{\"type\":\"thread.started\"}"));
        byte[] stderr = Enumerable.Repeat((byte)'s', 1200).ToArray();
        var child = FakeChild.Running(output, stderr);
        var runner = new CodexExecRunner(new FakeLauncher(child));

        CodexExecRunResult result = await runner.RunAsync(
            Configuration(maximumStandardErrorBytes: 1024),
            RunId);

        Assert.Equal(CodexExecRunStatus.AdapterRejected, result.Status);
        Assert.Equal("codex_exec.stderr_limit", result.ReasonCode);
        Assert.Equal(1025, result.StandardErrorByteCount);
        Assert.True(result.StandardErrorLimitExceeded);
        Assert.True(child.Killed);
        Assert.Equal(CompanionBodyState.Disconnected, result.EventStream.FinalProjection.BodyState);
    }

    [Fact]
    public async Task MissingOptionalExecutableFailsVisiblyWithoutCallingLauncher()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-missing-codex-{Guid.NewGuid():N}");
        CodexExecConfiguration configuration = CodexExecConfiguration.Create(
            missing,
            Environment.CurrentDirectory,
            "prompt").Value!;
        var launcher = new FakeLauncher(
            FakeChild.Exited([], [], exitCode: 0));
        var runner = new CodexExecRunner(launcher);

        CodexExecRunResult result = await runner.RunAsync(configuration, RunId);

        Assert.Equal(CodexExecRunStatus.LaunchFailed, result.Status);
        Assert.Equal("codex_exec.executable_not_found", result.ReasonCode);
        Assert.Equal(CompanionBodyState.Disconnected, result.EventStream.FinalProjection.BodyState);
        Assert.Equal(0, launcher.StartCount);
    }

    private static CodexExecConfiguration Configuration(
        string prompt = "approved fixture prompt",
        TimeSpan? timeout = null,
        int maximumStandardErrorBytes = 64 * 1024) =>
        CodexExecConfiguration.Create(
            ExistingExecutablePath(),
            Environment.CurrentDirectory,
            prompt,
            timeout,
            maximumStandardErrorBytes).Value!;

    private static string ExistingExecutablePath() =>
        Path.GetFullPath(Environment.ProcessPath!);

    private static byte[] Jsonl(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");

    private sealed class FakeLauncher(FakeChild child) : ICodexProcessLauncher
    {
        public int StartCount { get; private set; }

        public ProcessStartInfo? StartInfo { get; private set; }

        public ICodexChildProcess Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            StartInfo = startInfo;
            return child;
        }
    }

    private sealed class FakeChild : ICodexChildProcess
    {
        private readonly TaskCompletionSource exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PrefixThenBlockingStream? blockingOutput;
        private readonly MemoryStream standardInput = new();
        private readonly Stream standardOutput;
        private readonly Stream standardError;
        private int exitCode;

        private FakeChild(
            Stream standardOutput,
            byte[] stderr,
            int exitCode,
            bool exited)
        {
            this.standardOutput = standardOutput;
            blockingOutput = standardOutput as PrefixThenBlockingStream;
            standardError = new MemoryStream(stderr, writable: false);
            this.exitCode = exitCode;
            if (exited)
            {
                exit.SetResult();
            }
        }

        public Stream StandardInput => standardInput;

        public Stream StandardOutput => standardOutput;

        public Stream StandardError => standardError;

        public int ExitCode => exitCode;

        public bool Killed { get; private set; }

        public int KillCount { get; private set; }

        public byte[] WrittenStandardInput => standardInput.ToArray();

        public static FakeChild Exited(byte[] stdout, byte[] stderr, int exitCode) =>
            new(
                new MemoryStream(stdout, writable: false),
                stderr,
                exitCode,
                exited: true);

        public static FakeChild Running(
            PrefixThenBlockingStream stdout,
            byte[] stderr) =>
            new(stdout, stderr, exitCode: -1, exited: false);

        public Task WaitForExitAsync() => exit.Task;

        public void KillTree()
        {
            Killed = true;
            KillCount++;
            exitCode = -1;
            blockingOutput?.Complete();
            exit.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PrefixThenBlockingStream(byte[] prefix) : Stream
    {
        private readonly TaskCompletionSource completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public void Complete() => completed.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (position < prefix.Length)
            {
                int count = Math.Min(buffer.Length, prefix.Length - position);
                prefix.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            await completed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

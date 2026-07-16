using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Skills;

namespace Tooltail.SkillFixtureCli;

public static class FixtureCliApplication
{
    private const string ResultContractVersion = "tooltail.fixture-result/1";

    public static async Task<int> RunAsync(
        string[] arguments,
        TextWriter output,
        IClock clock,
        IIdGenerator idGenerator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        FixtureCommandLine parsed = FixtureCommandLine.Parse(arguments);
        if (!parsed.IsValid)
        {
            return await WriteAsync(
                output,
                parsed.Command,
                FixturePipelineResult.Failure(parsed.ReasonCode, exitCode: 2))
                .ConfigureAwait(false);
        }

        string command = parsed.Command;
        if (command is "help" or "--help" or "-h")
        {
            await output.WriteAsync(HelpText).ConfigureAwait(false);
            return 0;
        }

        if (command is "version" or "--version")
        {
            await output.WriteLineAsync(
                typeof(FixtureCliApplication).Assembly.GetName().Version?.ToString() ?? "0.0.0")
                .ConfigureAwait(false);
            return 0;
        }

        try
        {
            FixturePipelineResult result;
            if (command == "init-fixture")
            {
                result = await InitializeAsync(
                    parsed,
                    clock,
                    idGenerator,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (command == "golden-suite")
            {
                result = await RunGoldenSuiteAsync(
                    parsed,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!parsed.TrySingle("workspace", out string? workspacePath))
                {
                    result = FixturePipelineResult.Failure(
                        "fixture.workspace_required",
                        exitCode: 2);
                    return await WriteAsync(output, command, result).ConfigureAwait(false);
                }

                FixtureWorkspaceResult opened = await FixtureWorkspace.OpenAsync(
                    workspacePath!,
                    cancellationToken).ConfigureAwait(false);
                if (!opened.IsSuccess)
                {
                    result = FixturePipelineResult.Failure(opened.ReasonCode);
                }
                else
                {
                    result = await DispatchAsync(
                        command,
                        parsed,
                        opened.Workspace!,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return await WriteAsync(output, command, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await WriteAsync(
                output,
                command,
                FixturePipelineResult.Failure("fixture.cancelled"))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return await WriteAsync(
                output,
                command,
                FixturePipelineResult.Failure("fixture.command_failed"))
                .ConfigureAwait(false);
        }
    }

    private static Task<FixturePipelineResult> RunGoldenSuiteAsync(
        FixtureCommandLine arguments,
        CancellationToken cancellationToken)
    {
        if (!arguments.Allowed("workspace") ||
            !arguments.TrySingle("workspace", out string? workspacePath))
        {
            return Task.FromResult(
                FixturePipelineResult.Failure(
                    "fixture.golden_suite_arguments_invalid",
                    exitCode: 2));
        }

        return GoldenScenarioSuite.RunAsync(workspacePath!, cancellationToken);
    }

    private static async Task<FixturePipelineResult> InitializeAsync(
        FixtureCommandLine arguments,
        IClock clock,
        IIdGenerator idGenerator,
        CancellationToken cancellationToken)
    {
        if (!arguments.Allowed("workspace", "name", "description") ||
            !arguments.TrySingle("workspace", out string? workspacePath))
        {
            return FixturePipelineResult.Failure(
                "fixture.init_arguments_invalid",
                exitCode: 2);
        }

        string name = arguments.SingleOrDefault("name") ?? "Fixture learned skill";
        string description = arguments.SingleOrDefault("description") ??
            "A deterministic Tooltail-owned fixture workflow.";
        DateTimeOffset now = clock.UtcNow.ToUniversalTime();
        FixtureWorkspaceResult created = await FixtureWorkspace.CreateAsync(
            workspacePath!,
            idGenerator.NewId(),
            now,
            name,
            description,
            cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return FixturePipelineResult.Failure(created.ReasonCode);
        }

        FixtureValueResult<FixtureRuntime> runtime = await FixtureRuntime.CreateAsync(
            created.Workspace!,
            clockMinute: 0,
            cancellationToken).ConfigureAwait(false);
        return runtime.IsSuccess
            ? FixturePipelineResult.Success(
                "fixture.workspace_initialized",
                new
                {
                    WorkspaceId = created.Workspace!.Manifest.WorkspaceId,
                    Root = "root",
                    Artifacts = "artifacts",
                    State = "state/tooltail.db",
                    Temporary = "temp",
                })
            : FixturePipelineResult.Failure(runtime.ReasonCode);
    }

    private static Task<FixturePipelineResult> DispatchAsync(
        string command,
        FixtureCommandLine arguments,
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        return command switch
        {
            "snapshot" or "observe-fixture" => SnapshotAsync(
                arguments,
                workspace,
                cancellationToken),
            "reconcile" => ReconcileAsync(arguments, workspace, cancellationToken),
            "compile" => CompileAsync(arguments, workspace, cancellationToken),
            "validate" when arguments.Allowed("workspace") =>
                FixturePipeline.ValidateAsync(workspace, cancellationToken),
            "plan" when arguments.Allowed("workspace") =>
                FixturePipeline.PlanAsync(workspace, cancellationToken),
            "rehearse" when arguments.Allowed("workspace") =>
                FixturePipeline.RehearseAsync(workspace, cancellationToken),
            "execute-fixture" when arguments.Allowed("workspace") =>
                FixturePipeline.ExecuteAsync(workspace, cancellationToken),
            "verify" when arguments.Allowed("workspace") =>
                FixturePipeline.VerifyAsync(workspace, cancellationToken),
            "undo-fixture" when arguments.Allowed("workspace") =>
                FixturePipeline.UndoAsync(workspace, cancellationToken),
            "export-capsule" when arguments.Allowed("workspace") =>
                FixturePipeline.ExportCapsuleAsync(workspace, cancellationToken),
            _ => Task.FromResult(
                FixturePipelineResult.Failure(
                    "fixture.command_or_arguments_invalid",
                    exitCode: 2)),
        };
    }

    private static Task<FixturePipelineResult> SnapshotAsync(
        FixtureCommandLine arguments,
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!arguments.Allowed("workspace", "phase") ||
            !arguments.TrySingle("phase", out string? phase))
        {
            return Task.FromResult(
                FixturePipelineResult.Failure(
                    "fixture.snapshot_arguments_invalid",
                    exitCode: 2));
        }

        return FixturePipeline.SnapshotAsync(workspace, phase!, cancellationToken);
    }

    private static Task<FixturePipelineResult> ReconcileAsync(
        FixtureCommandLine arguments,
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!arguments.Allowed("workspace", "overflow"))
        {
            return Task.FromResult(
                FixturePipelineResult.Failure(
                    "fixture.reconcile_arguments_invalid",
                    exitCode: 2));
        }

        return FixturePipeline.ReconcileAsync(
            workspace,
            watcherOverflow: arguments.HasFlag("overflow"),
            cancellationToken: cancellationToken);
    }

    private static Task<FixturePipelineResult> CompileAsync(
        FixtureCommandLine arguments,
        FixtureWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!arguments.Allowed("workspace", "answer"))
        {
            return Task.FromResult(
                FixturePipelineResult.Failure(
                    "fixture.compile_arguments_invalid",
                    exitCode: 2));
        }

        string[] values = arguments.Values("answer");
        if (values.Length > 16)
        {
            return Task.FromResult(
                FixturePipelineResult.Failure(
                    "fixture.answer_limit_exceeded",
                    exitCode: 2));
        }

        List<SkillUserAnswerContract> answers = [];
        foreach (string value in values)
        {
            int separator = value.IndexOf('=', StringComparison.Ordinal);
            if (separator is <= 0 || separator == value.Length - 1)
            {
                return Task.FromResult(
                    FixturePipelineResult.Failure(
                        "fixture.answer_invalid",
                        exitCode: 2));
            }

            answers.Add(
                new SkillUserAnswerContract
                {
                    QuestionCode = value[..separator],
                    SelectedValue = value[(separator + 1)..],
                });
        }

        return FixturePipeline.CompileAsync(
            workspace,
            answers,
            cancellationToken);
    }

    private static async Task<int> WriteAsync(
        TextWriter output,
        string command,
        FixturePipelineResult result)
    {
        FixtureCommandEnvelope envelope = new()
        {
            ContractVersion = ResultContractVersion,
            Command = command,
            Status = result.IsSuccess ? "succeeded" : "failed",
            ReasonCode = result.ReasonCode,
            Data = result.Data,
        };
        await output.WriteLineAsync(Encoding.UTF8.GetString(FixtureJson.Serialize(envelope)))
            .ConfigureAwait(false);
        return result.ExitCode;
    }

    private const string HelpText =
        "Tooltail Skill Fixture CLI\n\n" +
        "Usage: tooltail-skill-fixture <command> --workspace <absolute-owned-path> [options]\n\n" +
        "Commands:\n" +
        "  init-fixture     Create a new marked Tooltail-owned fixture workspace.\n" +
        "  snapshot         Capture --phase baseline, final, or planning.\n" +
        "  observe-fixture  Alias for snapshot.\n" +
        "  reconcile        Reconcile baseline/final snapshots; --overflow fails closed.\n" +
        "  compile          Compile evidence; repeat --answer code=value as needed.\n" +
        "  validate         Validate the canonical SkillSpec artifact.\n" +
        "  plan             Capture a fresh fixture and emit an exact canonical plan.\n" +
        "  rehearse         Run the shared executor in the owned temp root.\n" +
        "  execute-fixture  Approve once, execute, verify, and persist a receipt.\n" +
        "  verify           Reload and verify the persisted journal and receipt.\n" +
        "  undo-fixture     Plan, separately approve, execute, and verify undo.\n" +
        "  export-capsule   Export one validated fixture skill without authority.\n" +
        "  golden-suite     Run all six exact M2 scenarios in a new owned workspace.\n" +
        "  version          Print the tool assembly version.\n" +
        "  help             Show this help without accessing user data.\n\n" +
        "No command defaults to Desktop, Documents, or the current directory.\n";
}

internal sealed record FixtureCommandEnvelope
{
    public required string ContractVersion { get; init; }

    public required string Command { get; init; }

    public required string Status { get; init; }

    public required string ReasonCode { get; init; }

    public object? Data { get; init; }
}

internal sealed class FixtureCommandLine
{
    private readonly Dictionary<string, List<string>> options = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    private FixtureCommandLine(string command, bool isValid, string reasonCode)
    {
        Command = command;
        IsValid = isValid;
        ReasonCode = reasonCode;
    }

    public string Command { get; }

    public bool IsValid { get; private set; }

    public string ReasonCode { get; private set; }

    public static FixtureCommandLine Parse(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new FixtureCommandLine("help", isValid: true, "fixture.help");
        }

        string command = arguments[0].ToLowerInvariant();
        FixtureCommandLine result = new(command, isValid: true, "fixture.arguments_valid");
        if (arguments.Length > 65 || arguments.Any(static value =>
                value is null || value.Length > 4_096 || value.Any(char.IsControl)))
        {
            return result.Invalidate("fixture.arguments_unbounded");
        }

        for (int index = 1; index < arguments.Length; index++)
        {
            string token = arguments[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
            {
                return result.Invalidate("fixture.argument_syntax_invalid");
            }

            string key = token[2..];
            if (key == "overflow")
            {
                if (!result.flags.Add(key))
                {
                    return result.Invalidate("fixture.argument_duplicate");
                }

                continue;
            }

            if (index + 1 >= arguments.Length ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return result.Invalidate("fixture.argument_value_missing");
            }

            string value = arguments[++index];
            if (!result.options.TryGetValue(key, out List<string>? values))
            {
                values = [];
                result.options.Add(key, values);
            }

            if (key != "answer" && values.Count > 0)
            {
                return result.Invalidate("fixture.argument_duplicate");
            }

            values.Add(value);
        }

        return result;
    }

    public bool Allowed(params string[] names)
    {
        HashSet<string> allowed = names.ToHashSet(StringComparer.Ordinal);
        return options.Keys.All(allowed.Contains) && flags.All(allowed.Contains);
    }

    public bool TrySingle(string key, out string? value)
    {
        value = SingleOrDefault(key);
        return value is not null;
    }

    public string? SingleOrDefault(string key) =>
        options.TryGetValue(key, out List<string>? values) && values.Count == 1
            ? values[0]
            : null;

    public string[] Values(string key) =>
        options.TryGetValue(key, out List<string>? values) ? values.ToArray() : [];

    public bool HasFlag(string key) => flags.Contains(key);

    private FixtureCommandLine Invalidate(string reasonCode)
    {
        IsValid = false;
        ReasonCode = reasonCode;
        return this;
    }
}

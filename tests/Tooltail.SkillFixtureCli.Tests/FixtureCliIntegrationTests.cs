using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Capsules;
using Tooltail.Contracts.Json;
using Tooltail.SkillFixtureCli;
using Tooltail.Testing;

namespace Tooltail.SkillFixtureCli.Tests;

public sealed class FixtureCliIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExplicitOwnedWorkspaceRunsMoveRehearsalExecutionVerificationAndUndo()
    {
        using TemporaryDirectory parent = new();
        string workspace = Path.Combine(parent.Path, "fixture");
        JsonElement initialized = await AssertCommandAsync(
            expectedExitCode: 0,
            "init-fixture",
            "--workspace",
            workspace,
            "--name",
            "File invoice PDFs",
            "--description",
            "Move invoice PDFs into Invoices without overwriting anything.");
        Assert.Equal("fixture.workspace_initialized", Reason(initialized));
        string root = Path.Combine(workspace, "root");
        WriteFile(root, "Inbox/invoice-one.pdf", "invoice one", Now.AddDays(-2));
        WriteFile(root, "Inbox/invoice-two.pdf", "invoice two", Now.AddDays(-1));
        WriteFile(root, "Inbox/notes.txt", "notes", Now.AddDays(-1));

        await AssertSucceededAsync("snapshot", "--workspace", workspace, "--phase", "baseline");
        Directory.CreateDirectory(Path.Combine(root, "Invoices"));
        File.Move(
            Path.Combine(root, "Inbox", "invoice-one.pdf"),
            Path.Combine(root, "Invoices", "invoice-one.pdf"));
        File.Move(
            Path.Combine(root, "Inbox", "invoice-two.pdf"),
            Path.Combine(root, "Invoices", "invoice-two.pdf"));
        await AssertSucceededAsync("snapshot", "--workspace", workspace, "--phase", "final");
        await AssertSucceededAsync("reconcile", "--workspace", workspace);
        JsonElement compiled = await AssertCommandAsync(
            expectedExitCode: 0,
            "compile",
            "--workspace",
            workspace,
            "--answer",
            "match.origin_scope=same_directory",
            "--answer",
            "match.filename_scope=contains_token");
        Assert.Equal("compiler.ready", Reason(compiled));
        await AssertSucceededAsync("validate", "--workspace", workspace);

        Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(Path.Combine(root, "Inbox"));
        Directory.CreateDirectory(Path.Combine(root, "Invoices"));
        WriteFile(root, "Inbox/invoice-three.pdf", "invoice three", Now.AddDays(-3));
        WriteFile(root, "Inbox/invoice-four.pdf", "invoice four", Now.AddDays(-4));
        WriteFile(root, "Inbox/notes.txt", "notes", Now.AddDays(-1));
        string[] expectedOriginalTree = RelativeTree(root);

        await AssertSucceededAsync("plan", "--workspace", workspace);
        await AssertSucceededAsync("rehearse", "--workspace", workspace);
        await AssertSucceededAsync("execute-fixture", "--workspace", workspace);
        await AssertSucceededAsync("verify", "--workspace", workspace);
        Assert.True(File.Exists(Path.Combine(root, "Invoices", "invoice-three.pdf")));
        Assert.True(File.Exists(Path.Combine(root, "Invoices", "invoice-four.pdf")));

        JsonElement exported = await AssertCommandAsync(
            expectedExitCode: 0,
            "export-capsule",
            "--workspace",
            workspace);
        Assert.Equal("fixture.capsule_exported", Reason(exported));
        JsonElement capsuleData = exported.GetProperty("data").GetProperty("capsule");
        Assert.Equal(
            "require_user_rebind",
            capsuleData.GetProperty("skills")[0]
                .GetProperty("sourceGrantBinding")
                .GetProperty("importBehavior")
                .GetString());
        Assert.All(
            capsuleData.GetProperty("contentPolicy").EnumerateObject(),
            static property => Assert.False(property.Value.GetBoolean()));
        byte[] capsuleBytes = await File.ReadAllBytesAsync(
            Path.Combine(workspace, "artifacts", "companion-capsule.json"));
        ContractParseResult<CompanionCapsuleContract> parsedCapsule =
            ContractJson.ParseCompanionCapsule(capsuleBytes);
        Assert.True(parsedCapsule.IsSuccess, parsedCapsule.Error?.Code);
        Assert.Equal(1, parsedCapsule.Value!.Skills[0].EvidenceSummary.VerifiedSuccessCount);
        Assert.DoesNotContain(
            parent.Path,
            Encoding.UTF8.GetString(capsuleBytes),
            StringComparison.OrdinalIgnoreCase);

        WriteFile(root, "unexpected.txt", "post-execution mutation", Now);
        JsonElement changed = await AssertCommandAsync(
            expectedExitCode: 1,
            "verify",
            "--workspace",
            workspace);
        Assert.Equal("fixture.execution_tree_changed", Reason(changed));
        File.Delete(Path.Combine(root, "unexpected.txt"));

        JsonElement undone = await AssertCommandAsync(
            expectedExitCode: 0,
            "undo-fixture",
            "--workspace",
            workspace);

        Assert.Equal("undo.verified", Reason(undone));
        Assert.Equal(expectedOriginalTree, RelativeTree(root));
    }

    [Fact]
    public async Task HelpAndInvalidPathsNeverDefaultToUserData()
    {
        JsonElement rejected = await AssertCommandAsync(
            expectedExitCode: 1,
            "init-fixture",
            "--workspace",
            "relative-fixture");
        using StringWriter output = new();
        int helpExit = await FixtureCliApplication.RunAsync(
            ["--help"],
            output,
            new FixedClock(),
            new FixedIdGenerator());

        Assert.Equal("fixture.workspace_must_be_new", Reason(rejected));
        Assert.Equal(0, helpExit);
        Assert.Contains("No command defaults", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SixScenarioSuiteMatchesCommittedCrossPlatformGoldenExactly()
    {
        using TemporaryDirectory parent = new();
        string workspace = Path.Combine(parent.Path, "golden-suite");
        using StringWriter output = new();

        int exitCode = await FixtureCliApplication.RunAsync(
            ["golden-suite", "--workspace", workspace],
            output,
            new FixedClock(),
            new FixedIdGenerator());

        Assert.Equal(0, exitCode);
        string normalized = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain(parent.Path, normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "30ab4fe4e20ce99088820e0ea9a25aa46d971d8e05fa714c385af303d966d75b",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
        using JsonDocument actual = JsonDocument.Parse(normalized);
        string expectedPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "golden-suite.expected.json");
        using JsonDocument expected = JsonDocument.Parse(await File.ReadAllBytesAsync(expectedPath));
        Assert.True(
            JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
            "The complete six-scenario machine-readable output changed.");
    }

    [Fact]
    public async Task WorkspaceCreationRejectsAReparsePointAnywhereInItsAncestry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory parent = new();
        string target = Path.Combine(parent.Path, "target");
        string link = Path.Combine(parent.Path, "linked-parent");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        JsonElement rejected = await AssertCommandAsync(
            expectedExitCode: 1,
            "init-fixture",
            "--workspace",
            Path.Combine(link, "fixture"));

        Assert.Equal("fixture.workspace_parent_invalid", Reason(rejected));
        Assert.False(Directory.Exists(Path.Combine(target, "fixture")));
    }

    [Fact]
    public async Task ArtifactReadsRejectAReparsePointInsertedAfterWorkspaceCreation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory parent = new();
        string workspace = Path.Combine(parent.Path, "fixture");
        await AssertSucceededAsync("init-fixture", "--workspace", workspace);
        string outside = Path.Combine(parent.Path, "outside.json");
        await File.WriteAllTextAsync(outside, "{}");
        string artifact = Path.Combine(workspace, "artifacts", "skill-spec.json");
        File.CreateSymbolicLink(artifact, outside);

        JsonElement rejected = await AssertCommandAsync(
            expectedExitCode: 1,
            "validate",
            "--workspace",
            workspace);

        Assert.Equal("fixture.skill_artifact_missing", Reason(rejected));
        Assert.Equal("{}", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task StateDatabaseRejectsAReparsePointInsertedAfterWorkspaceCreation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory parent = new();
        string workspace = Path.Combine(parent.Path, "fixture");
        await AssertSucceededAsync("init-fixture", "--workspace", workspace);
        string database = Path.Combine(workspace, "state", "tooltail.db");
        File.Delete(database);
        string outside = Path.Combine(parent.Path, "outside.db");
        await File.WriteAllTextAsync(outside, "do not touch");
        File.CreateSymbolicLink(database, outside);

        JsonElement rejected = await AssertCommandAsync(
            expectedExitCode: 1,
            "snapshot",
            "--workspace",
            workspace,
            "--phase",
            "baseline");

        Assert.Equal("fixture.state_storage_unsafe", Reason(rejected));
        Assert.Equal("do not touch", await File.ReadAllTextAsync(outside));
    }

    private static async Task AssertSucceededAsync(params string[] arguments)
    {
        JsonElement result = await AssertCommandAsync(0, arguments);
        Assert.Equal("succeeded", result.GetProperty("status").GetString());
    }

    private static async Task<JsonElement> AssertCommandAsync(
        int expectedExitCode,
        params string[] arguments)
    {
        using StringWriter output = new();
        int exitCode = await FixtureCliApplication.RunAsync(
            arguments,
            output,
            new FixedClock(),
            new FixedIdGenerator());
        Assert.True(
            exitCode == expectedExitCode,
            $"Expected exit {expectedExitCode}, received {exitCode}: {output}");
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return document.RootElement.Clone();
    }

    private static string Reason(JsonElement result) =>
        result.GetProperty("reasonCode").GetString()!;

    private static void WriteFile(
        string root,
        string relativePath,
        string content,
        DateTimeOffset lastWriteUtc)
    {
        string path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, Now.AddDays(-10).UtcDateTime);
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
    }

    private static string[] RelativeTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FixedIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.Parse("11111111-1111-4111-8111-111111111111");
    }
}

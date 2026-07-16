namespace Tooltail.Architecture.Tests;

public sealed class WindowLeaseBoundaryTests
{
    [Fact]
    public void WindowBindingCannotReferenceResourceGrantOrEffectAuthority()
    {
        string bindingRoot = Path.Combine(
            RepositoryLayout.FindRoot(),
            "src",
            "Tooltail.Application",
            "Windows");
        string source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(bindingRoot, "*.cs").Select(File.ReadAllText));
        string[] forbiddenAuthorityTokens =
        [
            "LocalFolderGrant",
            "ResourceGrant",
            "GrantCapability",
            "PermissionGateway",
            "ExecutionPlan",
            "ApprovalId",
            "System.IO.File",
        ];

        Assert.All(
            forbiddenAuthorityTokens,
            token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
    }

    [Fact]
    public void NativeWindowBoundaryHasNoInputInjectionOrFocusActivationApi()
    {
        string windowingRoot = Path.Combine(
            RepositoryLayout.FindRoot(),
            "src",
            "Tooltail.Platform.Windows",
            "Windowing");
        string source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(windowingRoot, "*.cs").Select(File.ReadAllText));
        string[] forbiddenApis =
        [
            "SendInput",
            "SetCursorPos",
            "mouse_event",
            "keybd_event",
            "SetForegroundWindow",
            "AttachThreadInput",
            "PostMessage(",
            "SendMessage(",
        ];

        Assert.All(
            forbiddenApis,
            api => Assert.DoesNotContain(api, source, StringComparison.Ordinal));
        Assert.Contains("MaximumPendingSignals", source, StringComparison.Ordinal);
        Assert.Contains("WinEventOutOfContext", source, StringComparison.Ordinal);
        Assert.Contains("UnhookWinEvent", source, StringComparison.Ordinal);
    }
}

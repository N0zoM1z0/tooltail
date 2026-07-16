using Microsoft.Win32;
using Tooltail.Application.Abstractions;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.FileSystem;

public sealed class WindowsFileSystemPathProbeTests
{
    [WindowsFact]
    [Trait("Platform", "Windows")]
    public void ProbeCapturesStableIdentityForTooltailOwnedTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-path-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            WindowsFileSystemPathProbe probe = new();

            FileSystemPathProbeResult first = probe.Probe(directory);
            FileSystemPathProbeResult second = probe.Probe(directory);

            Assert.Equal(FileSystemPathProbeStatus.Success, first.Status);
            Assert.Equal(FileSystemEntryKind.Directory, first.EntryKind);
            Assert.True(first.IsLocalFixedDrive);
            Assert.False(first.IsReparsePoint);
            Assert.Equal(first.VolumeIdentity, second.VolumeIdentity);
            Assert.Equal(first.EntryIdentity, second.EntryIdentity);
            Assert.Equal(first.CanonicalPath, second.CanonicalPath, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    [WindowsFact]
    [Trait("Platform", "Windows")]
    public void ProbeReportsMissingPathWithoutThrowing()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-path-probe-missing-{Guid.NewGuid():N}");

        FileSystemPathProbeResult result = new WindowsFileSystemPathProbe().Probe(missing);

        Assert.Equal(FileSystemPathProbeStatus.NotFound, result.Status);
        Assert.Equal("filesystem.not_found", result.ReasonCode);
    }

    [WindowsSymbolicLinkFact]
    [Trait("Platform", "Windows")]
    public void ProbeInspectsSymbolicLinkItselfWhenHostAllowsCreation()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            $"tooltail-path-probe-links-{Guid.NewGuid():N}");
        string target = Path.Combine(parent, "target");
        string link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);

            FileSystemPathProbeResult result = new WindowsFileSystemPathProbe().Probe(link);

            Assert.Equal(FileSystemPathProbeStatus.Success, result.Status);
            Assert.True(result.IsReparsePoint);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(parent, recursive: true);
        }
    }

    private class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, (int)sourceLineNumber)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires a Windows host.";
            }
        }
    }

    private sealed class WindowsSymbolicLinkFactAttribute : WindowsFactAttribute
    {
        public WindowsSymbolicLinkFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (OperatingSystem.IsWindows() && !IsDeveloperModeEnabled())
            {
                Skip = "Requires Windows Developer Mode for unprivileged symbolic-link creation.";
            }
        }

        private static bool IsDeveloperModeEnabled() =>
            Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock",
                "AllowDevelopmentWithoutDevLicense",
                defaultValue: 0) is 1;
    }
}

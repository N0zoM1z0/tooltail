using System.Text.Json;
using Tooltail.ReleaseAudit;

namespace Tooltail.ReleaseAudit.Tests;

public sealed class ReleaseAuditApplicationTests
{
    [Fact]
    public async Task TrackedRepositoryProducesReviewedSpdxAndEvidence()
    {
        string root = RepositoryRoot();
        string artifactRoot = Path.Combine(root, "artifacts", "release-audit");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ReleaseAuditApplication.RunAsync(
            ["verify", "--root", root, "--output", artifactRoot],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        Assert.Equal("release_audit.verified", result.RootElement
            .GetProperty("reasonCode").GetString());
        using JsonDocument spdx = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(artifactRoot, "tooltail.spdx.json")));
        Assert.Equal("SPDX-2.3", spdx.RootElement
            .GetProperty("spdxVersion").GetString());
        Assert.True(spdx.RootElement.GetProperty("packages").GetArrayLength() > 0);
        Assert.Contains(
            spdx.RootElement.GetProperty("hasExtractedLicensingInfos")
                .EnumerateArray(),
            item => item.GetProperty("licenseId").GetString() ==
                "LicenseRef-OSMFEULA");
    }

    [Fact]
    public async Task ArbitraryOutputPathIsRejectedWithoutCreatingIt()
    {
        string root = RepositoryRoot();
        string prohibited = Path.Combine(root, "not-release-audit-output");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ReleaseAuditApplication.RunAsync(
            ["verify", "--root", root, "--output", prohibited],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(prohibited));
        Assert.Contains("release_audit.failed", error.ToString(), StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Tooltail.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln.");
    }
}

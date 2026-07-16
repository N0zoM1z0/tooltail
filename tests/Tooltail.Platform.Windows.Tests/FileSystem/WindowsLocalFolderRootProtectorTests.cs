using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Platform.Windows.FileSystem;

namespace Tooltail.Platform.Windows.Tests.FileSystem;

public sealed class WindowsLocalFolderRootProtectorTests
{
    [WindowsFact]
    public void CurrentUserProtectionRoundTripsAndRejectsTampering()
    {
        WindowsLocalFolderRootProtector protector = new();
        string canonicalRoot = Path.Combine(
            Path.GetTempPath(),
            "Tooltail protected root",
            Guid.NewGuid().ToString("N"));

        RootProtectionResult protectedResult = protector.Protect(canonicalRoot);

        Assert.True(protectedResult.IsSuccess, protectedResult.ReasonCode);
        Assert.NotNull(protectedResult.ProtectedCanonicalRoot);
        Assert.Equal(
            -1,
            protectedResult.ProtectedCanonicalRoot!.AsSpan().IndexOf(
                Encoding.UTF8.GetBytes(canonicalRoot)));
        RootUnprotectionResult roundTrip = protector.Unprotect(
            protectedResult.ProtectedCanonicalRoot!);
        Assert.True(roundTrip.IsSuccess, roundTrip.ReasonCode);
        Assert.Equal(canonicalRoot, roundTrip.CanonicalRoot);

        byte[] tampered = protectedResult.ProtectedCanonicalRoot!.ToArray();
        tampered[^1] ^= 0x40;
        Assert.False(protector.Unprotect(tampered).IsSuccess);
    }

    [Fact]
    public void InvalidInputsFailWithoutNativeCalls()
    {
        WindowsLocalFolderRootProtector protector = new();

        Assert.False(protector.Protect(string.Empty).IsSuccess);
        Assert.False(protector.Unprotect([]).IsSuccess);
        Assert.False(protector.Unprotect(new byte[(64 * 1024) + 1]).IsSuccess);
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute(
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Requires Windows DPAPI under the current standard-user profile.";
            }
        }
    }
}

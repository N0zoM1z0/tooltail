namespace Tooltail.Application.Abstractions;

public sealed record RootProtectionResult(
    bool IsSuccess,
    string ReasonCode,
    byte[]? ProtectedCanonicalRoot);

public sealed record RootUnprotectionResult(
    bool IsSuccess,
    string ReasonCode,
    string? CanonicalRoot);

public interface ILocalFolderRootProtector
{
    RootProtectionResult Protect(string canonicalRoot);

    RootUnprotectionResult Unprotect(ReadOnlySpan<byte> protectedCanonicalRoot);
}

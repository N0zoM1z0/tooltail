using Tooltail.Domain.Permissions;

namespace Tooltail.Features.FileSkills.Grants;

public static class LocalFolderGrantPolicy
{
    public static IReadOnlyList<GrantCapability> FileApprenticeCapabilities { get; } =
        Array.AsReadOnly(
        [
            GrantCapability.Enumerate,
            GrantCapability.ReadMetadata,
            GrantCapability.ReadContentHash,
            GrantCapability.CreateDirectory,
            GrantCapability.Rename,
            GrantCapability.MoveWithinRoot,
            GrantCapability.CopyWithinRoot,
        ]);
}

using System.IO;
using System.Security.Cryptography;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Continuity;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Continuity;

public sealed record CapsuleFilePreviewResult(
    bool IsSuccess,
    string ReasonCode,
    int ByteCount,
    string? Sha256,
    CapsuleImportPreview? Preview,
    byte[]? ExactBytes);

public sealed class CapsuleImportFileWorkflowService
{
    private const string CapsuleSuffix = ".tooltail-capsule.json";
    private readonly WindowsPathSafetyService pathSafety;

    public CapsuleImportFileWorkflowService(WindowsPathSafetyService pathSafety)
    {
        ArgumentNullException.ThrowIfNull(pathSafety);
        this.pathSafety = pathSafety;
    }

    public async Task<CapsuleFilePreviewResult> PreviewAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) ||
            !selectedPath.EndsWith(CapsuleSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("capsule.file_name_invalid");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(selectedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return Failure("capsule.file_path_invalid");
        }

        string? parent = Path.GetDirectoryName(fullPath);
        string fileName = Path.GetFileName(fullPath);
        PathSafetyResult<CanonicalLocalRoot> root = pathSafety.CaptureRoot(parent);
        if (!root.IsSuccess)
        {
            return Failure(root.Error!.Code);
        }

        PathSafetyResult<BoundLocalPath> bound = pathSafety.Bind(
            root.Value!,
            fileName,
            PathEntryExpectation.MustExist);
        if (!bound.IsSuccess || bound.Value!.Components[^1].EntryKind !=
            FileSystemEntryKind.File)
        {
            return Failure(bound.IsSuccess
                ? "capsule.file_not_regular"
                : bound.Error!.Code);
        }

        long length;
        try
        {
            length = new FileInfo(bound.Value.FullPath).Length;
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("capsule.file_access_denied");
        }
        catch (IOException)
        {
            return Failure("capsule.file_io_failure");
        }

        if (length is < 2 or > ContractJson.CompanionCapsuleMaximumBytes)
        {
            return Failure("capsule.file_size_invalid");
        }

        byte[] bytes = new byte[checked((int)length)];
        try
        {
            await using FileStream stream = new(
                bound.Value.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1)
            {
                return Failure("capsule.file_changed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("capsule.file_access_denied");
        }
        catch (IOException)
        {
            return Failure("capsule.file_io_failure");
        }

        PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(bound.Value);
        if (!current.IsSuccess)
        {
            return Failure(current.Error!.Code);
        }

        CapsuleImportPreview preview = CompanionCapsuleService.ParseForImport(bytes);
        if (!preview.IsSuccess || !preview.CanImport || preview.CreatesAuthority ||
            !preview.SkillsRequireRebind)
        {
            return new CapsuleFilePreviewResult(
                false,
                preview.ReasonCode,
                bytes.Length,
                null,
                preview,
                null);
        }

        return new CapsuleFilePreviewResult(
            true,
            "capsule.file_preview_ready",
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            preview,
            bytes);
    }

    private static CapsuleFilePreviewResult Failure(string reasonCode) =>
        new(false, reasonCode, 0, null, null, null);
}

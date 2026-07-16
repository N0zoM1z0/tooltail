using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Diagnostics;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Infrastructure.Sqlite;

namespace Tooltail.Desktop.Presentation;

public sealed record DiagnosticPreviewWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    string PreviewJson,
    int ByteCount,
    string? Sha256,
    byte[]? ExactBytes,
    DiagnosticExportDocument? Document);

public sealed record DiagnosticExportWorkflowResult(
    bool IsSuccess,
    string ReasonCode,
    string? CanonicalPath,
    int ByteCount,
    string? Sha256);

public sealed class DiagnosticExportWorkflowService
{
    private readonly TooltailSqliteDatabase database;
    private readonly IFileSkillStateStore stateStore;
    private readonly IExecutionJournalReader journalReader;
    private readonly WindowsPathSafetyService pathSafety;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;

    public DiagnosticExportWorkflowService(
        TooltailSqliteDatabase database,
        IFileSkillStateStore stateStore,
        IExecutionJournalReader journalReader,
        WindowsPathSafetyService pathSafety,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(journalReader);
        ArgumentNullException.ThrowIfNull(pathSafety);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.database = database;
        this.stateStore = stateStore;
        this.journalReader = journalReader;
        this.pathSafety = pathSafety;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<DiagnosticPreviewWorkflowResult> PreviewAsync(
        CompanionId companionId,
        CompanionBodyProjection body,
        DiagnosticResearchSnapshot research,
        CancellationToken cancellationToken = default)
    {
        if (companionId.Value == Guid.Empty)
        {
            return PreviewFailure("diagnostic.companion_invalid");
        }

        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                companionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return PreviewFailure(workspace.ReasonCode);
        }

        ExecutionRecoveryScanResult recovery =
            await journalReader.ScanRecoveryRequiredAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!recovery.IsSuccess)
        {
            return PreviewFailure(recovery.ReasonCode);
        }

        DiagnosticEncodingResult encoded = DiagnosticExportBuilder.Build(
            workspace.Value!,
            recovery,
            body,
            research,
            clock.UtcNow,
            typeof(DiagnosticExportWorkflowService).Assembly
                .GetName().Version?.ToString() ?? "0.0.0");
        if (!encoded.IsSuccess || encoded.Bytes is null || encoded.Document is null)
        {
            return PreviewFailure(encoded.ReasonCode);
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(encoded.Bytes));
        return new DiagnosticPreviewWorkflowResult(
            true,
            "diagnostic.preview_ready",
            Encoding.UTF8.GetString(encoded.Bytes),
            encoded.Bytes.Length,
            hash,
            encoded.Bytes,
            encoded.Document);
    }

    public Task<DiagnosticExportWorkflowResult> ExportAsync(
        DiagnosticPreviewWorkflowResult preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();
        if (!preview.IsSuccess || preview.ExactBytes is null ||
            preview.Sha256 is null || preview.Document is null ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(preview.ExactBytes)),
                preview.Sha256,
                StringComparison.Ordinal))
        {
            return Task.FromResult(ExportFailure("diagnostic.preview_invalid"));
        }

        DiagnosticEncodingResult readback = DiagnosticExportBuilder.Parse(
            preview.ExactBytes);
        if (!readback.IsSuccess || readback.Document != preview.Document)
        {
            return Task.FromResult(ExportFailure(readback.ReasonCode));
        }

        PathSafetyResult<CanonicalLocalRoot> root = EnsureOwnedDiagnosticRoot();
        if (!root.IsSuccess)
        {
            return Task.FromResult(ExportFailure(root.Error!.Code));
        }

        string fileName =
            $"diagnostic-{idGenerator.NewId():N}.tooltail-diagnostic.json";
        PathSafetyResult<BoundLocalPath> destination = pathSafety.Bind(
            root.Value!,
            fileName,
            PathEntryExpectation.MustNotExist);
        if (!destination.IsSuccess)
        {
            return Task.FromResult(ExportFailure(destination.Error!.Code));
        }

        PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(
            destination.Value!);
        if (!current.IsSuccess)
        {
            return Task.FromResult(ExportFailure(current.Error!.Code));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using FileStream stream = new(
                current.Value!.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(preview.ExactBytes);
            stream.Flush(flushToDisk: true);
            return Task.FromResult(new DiagnosticExportWorkflowResult(
                true,
                "diagnostic.exported",
                current.Value.FullPath,
                preview.ExactBytes.Length,
                preview.Sha256));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ExportFailure("diagnostic.export_access_denied"));
        }
        catch (IOException)
        {
            return Task.FromResult(ExportFailure("diagnostic.export_io_failure"));
        }
    }

    private PathSafetyResult<CanonicalLocalRoot> EnsureOwnedDiagnosticRoot()
    {
        string? stateDirectory = Path.GetDirectoryName(database.DatabasePath);
        string? applicationRootPath = stateDirectory is null
            ? null
            : Path.GetDirectoryName(stateDirectory);
        PathSafetyResult<CanonicalLocalRoot> applicationRoot =
            pathSafety.CaptureRoot(applicationRootPath);
        if (!applicationRoot.IsSuccess)
        {
            return applicationRoot;
        }

        PathSafetyResult<BoundLocalPath> existing = pathSafety.Bind(
            applicationRoot.Value!,
            "Diagnostics",
            PathEntryExpectation.MayExist);
        if (!existing.IsSuccess)
        {
            return PathSafetyResult.Failure<CanonicalLocalRoot>(
                existing.Error!.Code,
                existing.Error.Message);
        }

        if (!existing.Value!.Components[^1].Existed)
        {
            PathSafetyResult<BoundLocalPath> current = pathSafety.Revalidate(existing.Value);
            if (!current.IsSuccess)
            {
                return PathSafetyResult.Failure<CanonicalLocalRoot>(
                    current.Error!.Code,
                    current.Error.Message);
            }

            Directory.CreateDirectory(current.Value!.FullPath);
        }

        return pathSafety.CaptureSubroot(applicationRoot.Value!, "Diagnostics");
    }

    private static DiagnosticPreviewWorkflowResult PreviewFailure(string reasonCode) =>
        new(false, reasonCode, string.Empty, 0, null, null, null);

    private static DiagnosticExportWorkflowResult ExportFailure(string reasonCode) =>
        new(false, reasonCode, null, 0, null);
}

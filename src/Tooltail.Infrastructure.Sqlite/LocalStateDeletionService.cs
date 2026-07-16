using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Json;

namespace Tooltail.Infrastructure.Sqlite;

public sealed record LocalStateDeletionPreview(
    bool IsSuccess,
    string ReasonCode,
    Guid? RequestId,
    DateTimeOffset? ExpiresUtc,
    IReadOnlyList<string> DeletedCategories,
    IReadOnlyList<string> PreservedCategories);

public sealed record LocalStateDeletionResult(
    bool IsSuccess,
    string ReasonCode,
    bool RequiresShutdown,
    bool RequiresRecovery);

public enum LocalStateDeletionBoundary
{
    IntentPersisted,
    DatabaseRemoved,
    SidecarsRemoved,
    IntentRemoved,
}

public interface ILocalStateDeletionFaultInjector
{
    void Reach(LocalStateDeletionBoundary boundary);
}

public sealed class NoLocalStateDeletionFaultInjector : ILocalStateDeletionFaultInjector
{
    public static NoLocalStateDeletionFaultInjector Instance { get; } = new();

    private NoLocalStateDeletionFaultInjector()
    {
    }

    public void Reach(LocalStateDeletionBoundary boundary)
    {
    }
}

public sealed class LocalStateDeletionService
{
    private const string IntentFileName = "local-state-deletion.intent.json";
    private const long MaximumIntentBytes = 4096;
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<string> DeletedCategories = Array.AsReadOnly(
    [
        "companion identity",
        "folder grants",
        "teaching evidence",
        "skill versions",
        "plans and approvals",
        "execution journals and receipts",
    ]);
    private static readonly IReadOnlyList<string> PreservedCategories = Array.AsReadOnly(
    [
        "safe lab files",
        "user files",
        "rehearsal residuals requiring inspection",
        "Companion Capsule exports",
        "redacted diagnostic exports",
        "separately copied research exports",
    ]);

    private readonly TooltailSqliteDatabase database;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private readonly object gate = new();
    private PendingAuthorization? pending;

    public LocalStateDeletionService(
        TooltailSqliteDatabase database,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.database = database;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public LocalStateDeletionPreview Prepare()
    {
        DateTimeOffset now;
        try
        {
            _ = ValidateExistingLayout(database.DatabasePath);
            now = RequireUtcNow();
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FailurePreview("local_state.delete_unavailable");
        }

        Guid requestId = idGenerator.NewId();
        if (requestId == Guid.Empty)
        {
            return FailurePreview("local_state.delete_id_invalid");
        }

        DateTimeOffset expires = now.Add(AuthorizationLifetime);
        lock (gate)
        {
            pending = new PendingAuthorization(requestId, expires);
        }

        return new LocalStateDeletionPreview(
            true,
            "local_state.delete_preview_ready",
            requestId,
            expires,
            DeletedCategories,
            PreservedCategories);
    }

    public async Task<LocalStateDeletionResult> DeleteAsync(
        Guid requestId,
        ILocalStateDeletionFaultInjector? faultInjector = null,
        CancellationToken cancellationToken = default)
    {
        PendingAuthorization authorization;
        lock (gate)
        {
            if (pending is null || pending.RequestId != requestId)
            {
                return Failure("local_state.delete_authorization_mismatch");
            }

            authorization = pending;
            pending = null;
        }

        DateTimeOffset now = RequireUtcNow();
        if (now >= authorization.ExpiresUtc)
        {
            return Failure("local_state.delete_authorization_expired");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageLayout layout = ValidateExistingLayout(database.DatabasePath);
            DeletionIntent intent = new(
                "tooltail.local-state-deletion/1",
                requestId,
                now,
                Fingerprint(layout.ApplicationRoot));
            await PersistIntentAsync(layout, intent, CancellationToken.None)
                .ConfigureAwait(false);
            (faultInjector ?? NoLocalStateDeletionFaultInjector.Instance).Reach(
                LocalStateDeletionBoundary.IntentPersisted);
            return CompleteCore(layout, faultInjector);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            return new LocalStateDeletionResult(
                false,
                "local_state.delete_recovery_required",
                RequiresShutdown: true,
                RequiresRecovery: true);
        }
    }

    public LocalStateDeletionResult RecoverPendingDeletion(
        ILocalStateDeletionFaultInjector? faultInjector = null)
    {
        StorageLayout layout;
        try
        {
            layout = GetConfiguredLayout(database.DatabasePath);
            if (!Directory.Exists(layout.StateRoot) &&
                !File.Exists(layout.StateRoot))
            {
                return NoRecovery();
            }

            ValidateExistingDirectories(layout);
            if (!File.Exists(layout.IntentPath) &&
                !Directory.Exists(layout.IntentPath))
            {
                return NoRecovery();
            }
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return RecoveryFailure();
        }

        try
        {
            DeletionIntent? intent = JsonSerializer.Deserialize<DeletionIntent>(
                ReadIntentBytes(layout.IntentPath),
                ContractJson.SerializerOptions);
            if (intent is null ||
                intent.SchemaVersion != "tooltail.local-state-deletion/1" ||
                intent.RequestId == Guid.Empty ||
                intent.RequestedUtc.Offset != TimeSpan.Zero ||
                !string.Equals(
                    intent.ApplicationRootFingerprint,
                    Fingerprint(layout.ApplicationRoot),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Local state deletion intent is invalid.");
            }

            LocalStateDeletionResult completed = CompleteCore(layout, faultInjector);
            return completed with
            {
                ReasonCode = "local_state.delete_recovered",
                RequiresShutdown = false,
            };
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return RecoveryFailure();
        }
    }

    private static LocalStateDeletionResult CompleteCore(
        StorageLayout layout,
        ILocalStateDeletionFaultInjector? faultInjector)
    {
        ILocalStateDeletionFaultInjector injector =
            faultInjector ?? NoLocalStateDeletionFaultInjector.Instance;
        DeleteExactFile(layout.DatabasePath);
        injector.Reach(LocalStateDeletionBoundary.DatabaseRemoved);
        DeleteExactFile(layout.WalPath);
        DeleteExactFile(layout.ShmPath);
        injector.Reach(LocalStateDeletionBoundary.SidecarsRemoved);
        DeleteExactFile(layout.IntentPath);
        injector.Reach(LocalStateDeletionBoundary.IntentRemoved);
        if (ExistsAsFileOrDirectory(layout.DatabasePath) ||
            ExistsAsFileOrDirectory(layout.WalPath) ||
            ExistsAsFileOrDirectory(layout.ShmPath) ||
            ExistsAsFileOrDirectory(layout.IntentPath))
        {
            throw new IOException("Local state deletion readback failed.");
        }

        return new LocalStateDeletionResult(
            true,
            "local_state.deleted_restart_required",
            RequiresShutdown: true,
            RequiresRecovery: false);
    }

    private static async Task PersistIntentAsync(
        StorageLayout layout,
        DeletionIntent intent,
        CancellationToken cancellationToken)
    {
        if (File.Exists(layout.IntentPath) || Directory.Exists(layout.IntentPath))
        {
            throw new InvalidDataException("A local state deletion intent already exists.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            intent,
            ContractJson.SerializerOptions);
        await using FileStream stream = new(
            layout.IntentPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        ValidateIntentFile(layout.IntentPath);
    }

    private static void DeleteExactFile(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                "Local state deletion found a directory in an exact file slot.");
        }

        if (!File.Exists(path))
        {
            return;
        }

        ValidateFile(path);
        File.Delete(path);
    }

    private static bool ExistsAsFileOrDirectory(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static StorageLayout ValidateExistingLayout(string databasePath)
    {
        StorageLayout layout = GetConfiguredLayout(databasePath);
        ValidateExistingDirectories(layout);
        return layout;
    }

    private static StorageLayout GetConfiguredLayout(string databasePath)
    {
        string fullDatabase = Path.GetFullPath(databasePath);
        string? stateRoot = Path.GetDirectoryName(fullDatabase);
        string? applicationRoot = stateRoot is null ? null : Path.GetDirectoryName(stateRoot);
        if (!Path.IsPathFullyQualified(fullDatabase) ||
            fullDatabase.StartsWith("\\\\", StringComparison.Ordinal) ||
            stateRoot is null || applicationRoot is null ||
            !string.Equals(Path.GetFileName(fullDatabase), "tooltail.db", PathComparison()) ||
            !string.Equals(Path.GetFileName(stateRoot), "state", PathComparison()))
        {
            throw new ArgumentException("Local state deletion requires the fixed Tooltail database layout.");
        }

        if (OperatingSystem.IsWindows())
        {
            string? volumeRoot = Path.GetPathRoot(fullDatabase);
            if (volumeRoot is null || new DriveInfo(volumeRoot).DriveType != DriveType.Fixed)
            {
                throw new ArgumentException(
                    "Local state deletion requires a fixed local volume.");
            }
        }

        return new StorageLayout(
            applicationRoot,
            stateRoot,
            fullDatabase,
            $"{fullDatabase}-wal",
            $"{fullDatabase}-shm",
            Path.Combine(stateRoot, IntentFileName));
    }

    private static void ValidateExistingDirectories(StorageLayout layout)
    {
        if (!Directory.Exists(layout.StateRoot) ||
            !Directory.Exists(layout.ApplicationRoot))
        {
            throw new InvalidDataException("Local state deletion storage is incomplete.");
        }

        for (DirectoryInfo? current = new(layout.ApplicationRoot);
             current is not null;
             current = current.Parent)
        {
            if (!current.Exists ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Local state deletion ancestry is unsafe.");
            }
        }

        ValidateDirectory(layout.StateRoot);
    }

    private static void ValidateDirectory(string path)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Local state deletion directory is unsafe.");
        }
    }

    private static void ValidateFile(string path)
    {
        FileInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Local state deletion file is unsafe.");
        }
    }

    private static void ValidateIntentFile(string path)
    {
        ValidateFile(path);
        long length = new FileInfo(path).Length;
        if (length is < 2 or > MaximumIntentBytes)
        {
            throw new InvalidDataException("Local state deletion intent size is invalid.");
        }
    }

    private static byte[] ReadIntentBytes(string path)
    {
        ValidateFile(path);
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length is < 2 or > MaximumIntentBytes)
        {
            throw new InvalidDataException("Local state deletion intent size is invalid.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Local state deletion intent changed while reading.");
        }

        return bytes;
    }

    private static string Fingerprint(string applicationRoot) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(applicationRoot))));

    private DateTimeOffset RequireUtcNow()
    {
        DateTimeOffset now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero
            ? now
            : throw new InvalidOperationException("Local state deletion requires UTC.");
    }

    private static LocalStateDeletionPreview FailurePreview(string reasonCode) =>
        new(false, reasonCode, null, null, [], []);

    private static LocalStateDeletionResult Failure(string reasonCode) =>
        new(false, reasonCode, RequiresShutdown: false, RequiresRecovery: false);

    private static LocalStateDeletionResult NoRecovery() =>
        new(
            true,
            "local_state.no_delete_recovery",
            RequiresShutdown: false,
            RequiresRecovery: false);

    private static LocalStateDeletionResult RecoveryFailure() =>
        new(
            false,
            "local_state.delete_recovery_failed",
            RequiresShutdown: true,
            RequiresRecovery: true);

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record PendingAuthorization(Guid RequestId, DateTimeOffset ExpiresUtc);

    private sealed record DeletionIntent(
        string SchemaVersion,
        Guid RequestId,
        DateTimeOffset RequestedUtc,
        string ApplicationRootFingerprint);

    private sealed record StorageLayout(
        string ApplicationRoot,
        string StateRoot,
        string DatabasePath,
        string WalPath,
        string ShmPath,
        string IntentPath);
}

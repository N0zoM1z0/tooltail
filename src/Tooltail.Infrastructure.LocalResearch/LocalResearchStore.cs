using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Research;

namespace Tooltail.Infrastructure.LocalResearch;

public sealed class LocalResearchStore : IAsyncDisposable
{
    private const string ConsentFileName = "consent.json";
    private const string EventsFileName = "events.jsonl";
    private const string ResearchDirectoryName = "Research";
    private const string ExportDirectoryName = "Exports";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string applicationRoot;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private ConsentState consent = ConsentState.Disabled;
    private Guid? sessionId;
    private byte[]? sessionSalt;
    private int nextSequence;
    private bool initialized;
    private bool disposed;

    public LocalResearchStore(
        LocalResearchOptions options,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        applicationRoot = ValidateApplicationRoot(options.ApplicationRootPath);
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public bool IsEnabled
    {
        get
        {
            lock (stateGate)
            {
                return consent.Enabled && sessionId is not null;
            }
        }
    }

    public Guid? StudyId
    {
        get
        {
            lock (stateGate)
            {
                return consent.StudyId;
            }
        }
    }

    public Guid? SessionId
    {
        get
        {
            lock (stateGate)
            {
                return sessionId;
            }
        }
    }

    public async Task<ResearchStoreStatus> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                return await StatusCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            string researchRoot = ResearchRoot;
            if (!Directory.Exists(researchRoot))
            {
                initialized = true;
                return DisabledStatus("research.off_by_default");
            }

            ValidateOwnedDirectory(researchRoot);
            string consentPath = ConsentPath;
            if (!File.Exists(consentPath))
            {
                initialized = true;
                return DisabledStatus("research.off_by_default");
            }

            ConsentState loadedConsent = await ReadConsentAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (stateGate)
            {
                consent = loadedConsent;
            }
            initialized = true;
            if (!loadedConsent.Enabled)
            {
                return await StatusCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            StartNewSession();
            ResearchWriteResult started = await AppendCoreAsync(
                new ResearchEventInput(
                    ResearchEventType.SessionStarted,
                    Success: true,
                    "research.session_started"),
                cancellationToken).ConfigureAwait(false);
            return started.IsSuccess
                ? await StatusCoreAsync(cancellationToken).ConfigureAwait(false)
                : FailureStatus(started.ReasonCode);
        }
        catch (InvalidDataException)
        {
            initialized = true;
            return FailureStatus("research.local_state_invalid");
        }
        catch (IOException)
        {
            initialized = true;
            return FailureStatus("research.local_io_failure");
        }
        catch (UnauthorizedAccessException)
        {
            initialized = true;
            return FailureStatus("research.local_access_denied");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ResearchStoreStatus> EnableAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureInitialized();
            if (IsEnabled)
            {
                return await StatusCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureOwnedDirectories();
            EventDocument existing = await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Events.Count != 0)
            {
                return FailureStatus("research.existing_data_requires_delete");
            }

            DateTimeOffset now = RequireUtcNow();
            ConsentState enabledConsent = new(
                SchemaVersion: ContractVersions.V1,
                Enabled: true,
                StudyId: idGenerator.NewId(),
                OptedInAt: now);
            lock (stateGate)
            {
                consent = enabledConsent;
            }

            StartNewSession();
            await WriteConsentAsync(cancellationToken).ConfigureAwait(false);
            ResearchWriteResult optedIn = await AppendCoreAsync(
                new ResearchEventInput(
                    ResearchEventType.StudyOptedIn,
                    Success: true,
                    "research.opted_in"),
                cancellationToken).ConfigureAwait(false);
            if (!optedIn.IsSuccess)
            {
                return FailureStatus(optedIn.ReasonCode);
            }

            ResearchWriteResult started = await AppendCoreAsync(
                new ResearchEventInput(
                    ResearchEventType.SessionStarted,
                    Success: true,
                    "research.session_started"),
                cancellationToken).ConfigureAwait(false);
            return started.IsSuccess
                ? await StatusCoreAsync(cancellationToken).ConfigureAwait(false)
                : FailureStatus(started.ReasonCode);
        }
        catch (InvalidDataException)
        {
            return FailureStatus("research.local_state_invalid");
        }
        catch (IOException)
        {
            return FailureStatus("research.local_io_failure");
        }
        catch (UnauthorizedAccessException)
        {
            return FailureStatus("research.local_access_denied");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ResearchWriteResult> RecordAsync(
        ResearchEventInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureInitialized();
            return IsEnabled
                ? await AppendCoreAsync(input, cancellationToken).ConfigureAwait(false)
                : new ResearchWriteResult(false, "research.consent_required", null);
        }
        catch (InvalidDataException)
        {
            return new ResearchWriteResult(false, "research.local_state_invalid", null);
        }
        catch (IOException)
        {
            return new ResearchWriteResult(false, "research.local_io_failure", null);
        }
        catch (UnauthorizedAccessException)
        {
            return new ResearchWriteResult(false, "research.local_access_denied", null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ResearchPreviewResult> PreviewAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureInitialized();
            EventDocument document = await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
            string text = StrictUtf8.GetString(document.Bytes);
            bool truncated = text.Length > LocalResearchOptions.MaximumPreviewCharacters;
            return new ResearchPreviewResult(
                true,
                "research.preview_ready",
                truncated ? text[..LocalResearchOptions.MaximumPreviewCharacters] : text,
                document.Events.Count,
                document.Bytes.Length,
                truncated);
        }
        catch (InvalidDataException)
        {
            return new ResearchPreviewResult(
                false,
                "research.local_state_invalid",
                string.Empty,
                0,
                0,
                false);
        }
        catch (IOException)
        {
            return new ResearchPreviewResult(
                false,
                "research.local_io_failure",
                string.Empty,
                0,
                0,
                false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ResearchPreviewResult(
                false,
                "research.local_access_denied",
                string.Empty,
                0,
                0,
                false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ResearchExportResult> ExportAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureInitialized();
            EventDocument document = await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
            if (document.Events.Count == 0)
            {
                return new ResearchExportResult(
                    false,
                    "research.no_events",
                    null,
                    0,
                    0);
            }

            EnsureOwnedDirectories();
            string name = $"research-{idGenerator.NewId():N}.jsonl";
            string destination = Path.Combine(ExportRoot, name);
            ValidateNewFileSlot(destination);
            await using FileStream stream = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(document.Bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new ResearchExportResult(
                true,
                "research.exported",
                destination,
                document.Events.Count,
                document.Bytes.Length);
        }
        catch (InvalidDataException)
        {
            return new ResearchExportResult(
                false,
                "research.local_state_invalid",
                null,
                0,
                0);
        }
        catch (IOException)
        {
            return new ResearchExportResult(
                false,
                "research.export_io_failure",
                null,
                0,
                0);
        }
        catch (UnauthorizedAccessException)
        {
            return new ResearchExportResult(
                false,
                "research.local_access_denied",
                null,
                0,
                0);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ResearchStoreStatus> DeleteAllAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureInitialized();
            string[] exports = [];
            if (Directory.Exists(ResearchRoot))
            {
                ValidateOwnedDirectory(ResearchRoot);
                if (Directory.Exists(ExportRoot))
                {
                    ValidateOwnedDirectory(ExportRoot);
                    exports = Directory.EnumerateFiles(ExportRoot).ToArray();
                    foreach (string path in exports)
                    {
                        string name = Path.GetFileName(path);
                        if (!IsResearchExportName(name))
                        {
                            throw new InvalidDataException("Unexpected research export name.");
                        }

                        ValidateExistingFile(path);
                    }
                }

            }

            DisableSession();
            EnsureOwnedDirectories();
            await WriteConsentAsync(cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(ResearchRoot))
            {
                await TruncateIfPresentAsync(EventsPath, cancellationToken).ConfigureAwait(false);
                foreach (string path in exports)
                {
                    await TruncateIfPresentAsync(path, cancellationToken).ConfigureAwait(false);
                }
            }

            return DisabledStatus("research.deleted_and_disabled");
        }
        catch (InvalidDataException)
        {
            return FailureStatus("research.local_state_invalid");
        }
        catch (IOException)
        {
            return FailureStatus("research.local_io_failure");
        }
        catch (UnauthorizedAccessException)
        {
            return FailureStatus("research.local_access_denied");
        }
        finally
        {
            gate.Release();
        }
    }

    public ResearchTokenResult TokenizeRelativePath(string relativePath)
    {
        if (!IsNormalizedRelativePath(relativePath))
        {
            return new ResearchTokenResult(false, "research.path_token_input_invalid", null);
        }

        byte[] salt;
        lock (stateGate)
        {
            if (!consent.Enabled || sessionId is null || sessionSalt is null)
            {
                return new ResearchTokenResult(false, "research.consent_required", null);
            }

            salt = sessionSalt.ToArray();
        }

        byte[] input = StrictUtf8.GetBytes(relativePath.Normalize(NormalizationForm.FormC));
        byte[] material = new byte[salt.Length + input.Length];
        salt.CopyTo(material, 0);
        input.CopyTo(material, salt.Length);
        string token = Convert.ToHexStringLower(SHA256.HashData(material));
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(material);
        return new ResearchTokenResult(true, "research.path_token_created", token);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        lock (stateGate)
        {
            if (sessionSalt is not null)
            {
                CryptographicOperations.ZeroMemory(sessionSalt);
                sessionSalt = null;
            }
        }

        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<ResearchWriteResult> AppendCoreAsync(
        ResearchEventInput input,
        CancellationToken cancellationToken)
    {
        Guid studyIdentity;
        Guid sessionIdentity;
        lock (stateGate)
        {
            if (!consent.Enabled || consent.StudyId is null || sessionId is null)
            {
                return new ResearchWriteResult(false, "research.consent_required", null);
            }

            studyIdentity = consent.StudyId.Value;
            sessionIdentity = sessionId.Value;
        }

        EventDocument current = await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
        if (current.Events.Count >= LocalResearchOptions.MaximumEventCount)
        {
            return new ResearchWriteResult(false, "research.event_limit", null);
        }

        ResearchEventContract researchEvent = new()
        {
            SchemaVersion = ContractVersions.V1,
            EventId = idGenerator.NewId(),
            StudyId = studyIdentity,
            SessionId = sessionIdentity,
            Sequence = nextSequence,
            OccurredAt = RequireUtcNow(),
            Type = input.Type,
            Success = input.Success,
            ReasonCode = input.ReasonCode,
            DurationMilliseconds = input.DurationMilliseconds,
            Count = input.Count,
            SkillVersion = input.SkillVersion,
            BodyState = input.BodyState,
            PathToken = input.PathToken,
            Rating = input.Rating,
        };
        byte[] bytes = ContractJson.Serialize(researchEvent);
        ContractParseResult<ResearchEventContract> parsed =
            ContractJson.ParseResearchEvent(bytes);
        if (!parsed.IsSuccess)
        {
            return new ResearchWriteResult(false, parsed.Error!.Code, null);
        }

        if (current.Bytes.Length + bytes.Length + 1 > LocalResearchOptions.MaximumEventBytes)
        {
            return new ResearchWriteResult(false, "research.byte_limit", null);
        }

        EnsureOwnedDirectories();
        ValidateAppendFileSlot(EventsPath);
        await using (FileStream stream = new(
            EventsPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        nextSequence++;
        EventDocument readback = await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
        if (readback.Events.Count != current.Events.Count + 1 ||
            readback.Events[^1].EventId != researchEvent.EventId)
        {
            throw new InvalidDataException("Research append readback mismatch.");
        }

        return new ResearchWriteResult(true, "research.event_recorded", researchEvent);
    }

    private async Task<EventDocument> ReadEventsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(EventsPath))
        {
            return new EventDocument([], []);
        }

        ValidateExistingFile(EventsPath);
        FileInfo info = new(EventsPath);
        if (info.Length > LocalResearchOptions.MaximumEventBytes)
        {
            throw new InvalidDataException("Research event bytes exceed the limit.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(EventsPath, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return new EventDocument([], bytes);
        }

        string text = StrictUtf8.GetString(bytes);
        if (!text.EndsWith('\n'))
        {
            throw new InvalidDataException("Research JSONL has an incomplete tail.");
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > LocalResearchOptions.MaximumEventCount)
        {
            throw new InvalidDataException("Research event count exceeds the limit.");
        }

        List<ResearchEventContract> events = new(lines.Length);
        HashSet<Guid> identities = [];
        foreach (string line in lines)
        {
            ContractParseResult<ResearchEventContract> parsed =
                ContractJson.ParseResearchEvent(StrictUtf8.GetBytes(line));
            if (!parsed.IsSuccess || !identities.Add(parsed.Value!.EventId))
            {
                throw new InvalidDataException("Research JSONL failed strict readback.");
            }

            events.Add(parsed.Value);
        }

        return new EventDocument(events, bytes);
    }

    private async Task<ResearchStoreStatus> StatusCoreAsync(
        CancellationToken cancellationToken)
    {
        EventDocument events = await ReadEventsAsync(cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            bool enabled = consent.Enabled && sessionId is not null;
            return new ResearchStoreStatus(
                true,
                enabled ? "research.enabled" : "research.disabled",
                enabled,
                consent.StudyId,
                sessionId,
                events.Events.Count,
                events.Bytes.Length);
        }
    }

    private async Task<ConsentState> ReadConsentAsync(CancellationToken cancellationToken)
    {
        ValidateExistingFile(ConsentPath);
        byte[] bytes = await File.ReadAllBytesAsync(ConsentPath, cancellationToken)
            .ConfigureAwait(false);
        ConsentState? value;
        try
        {
            value = JsonSerializer.Deserialize<ConsentState>(
                bytes,
                ContractJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Consent state is malformed.", exception);
        }

        if (value is null || value.SchemaVersion != ContractVersions.V1 ||
            (value.Enabled &&
             (value.StudyId is null || value.StudyId == Guid.Empty ||
              value.OptedInAt is null || value.OptedInAt.Value.Offset != TimeSpan.Zero)) ||
            (!value.Enabled &&
             (value.StudyId is not null || value.OptedInAt is not null)))
        {
            throw new InvalidDataException("Consent state is invalid.");
        }

        return value;
    }

    private async Task WriteConsentAsync(CancellationToken cancellationToken)
    {
        ConsentState snapshot;
        lock (stateGate)
        {
            snapshot = consent;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            ContractJson.SerializerOptions);
        ValidateReplaceableOwnedFile(ConsentPath);
        await using FileStream stream = new(
            ConsentPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TruncateIfPresentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        ValidateExistingFile(path);
        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureOwnedDirectories()
    {
        ValidateOwnedDirectory(applicationRoot);
        EnsureFixedDirectory(ResearchRoot);
        EnsureFixedDirectory(ExportRoot);
    }

    private static void EnsureFixedDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        ValidateOwnedDirectory(path);
    }

    private static string ValidateApplicationRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(full) ||
            full.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Directory.Exists(full))
        {
            throw new ArgumentException(
                "Research storage requires an existing local application root.",
                nameof(path));
        }

        for (DirectoryInfo? current = new(full); current is not null; current = current.Parent)
        {
            ValidateOwnedDirectory(current.FullName);
        }

        return full;
    }

    private static void ValidateOwnedDirectory(string path)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Research directory identity is unsafe.");
        }
    }

    private static void ValidateExistingFile(string path)
    {
        FileInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Research file identity is unsafe.");
        }
    }

    private static void ValidateAppendFileSlot(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("Research file slot is a directory.");
        }

        if (File.Exists(path))
        {
            ValidateExistingFile(path);
        }
    }

    private static void ValidateNewFileSlot(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new InvalidDataException("Research export destination already exists.");
        }
    }

    private static void ValidateReplaceableOwnedFile(string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidDataException("Research consent slot is a directory.");
        }

        if (File.Exists(path))
        {
            ValidateExistingFile(path);
        }
    }

    private DateTimeOffset RequireUtcNow()
    {
        DateTimeOffset now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero
            ? now
            : throw new InvalidOperationException("Research time must use UTC.");
    }

    private void EnsureInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("Research storage is not initialized.");
        }
    }

    private void StartNewSession()
    {
        lock (stateGate)
        {
            if (sessionSalt is not null)
            {
                CryptographicOperations.ZeroMemory(sessionSalt);
            }

            sessionId = idGenerator.NewId();
            sessionSalt = RandomNumberGenerator.GetBytes(32);
            nextSequence = 0;
        }
    }

    private void DisableSession()
    {
        lock (stateGate)
        {
            consent = ConsentState.Disabled;
            sessionId = null;
            if (sessionSalt is not null)
            {
                CryptographicOperations.ZeroMemory(sessionSalt);
                sessionSalt = null;
            }

            nextSequence = 0;
        }
    }

    private static bool IsNormalizedRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            Path.IsPathFullyQualified(value) || value.Contains(':') ||
            value.Contains('/') || value.StartsWith('\\') || value.EndsWith('\\'))
        {
            return false;
        }

        string[] segments = value.Split('\\');
        return segments.All(static segment =>
            segment.Length is >= 1 and <= 255 &&
            segment is not "." and not ".." &&
            !segment.Any(char.IsControl));
    }

    private static bool IsResearchExportName(string value) =>
        value.Length == "research-.jsonl".Length + 32 &&
        value.StartsWith("research-", StringComparison.Ordinal) &&
        value.EndsWith(".jsonl", StringComparison.Ordinal) &&
        value.AsSpan(9, 32).ToString().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string ResearchRoot => Path.Combine(applicationRoot, ResearchDirectoryName);

    private string ExportRoot => Path.Combine(ResearchRoot, ExportDirectoryName);

    private string ConsentPath => Path.Combine(ResearchRoot, ConsentFileName);

    private string EventsPath => Path.Combine(ResearchRoot, EventsFileName);

    private static ResearchStoreStatus DisabledStatus(string reasonCode) =>
        new(true, reasonCode, false, null, null, 0, 0);

    private static ResearchStoreStatus FailureStatus(string reasonCode) =>
        new(false, reasonCode, false, null, null, 0, 0);

    private sealed record ConsentState(
        string SchemaVersion,
        bool Enabled,
        Guid? StudyId,
        DateTimeOffset? OptedInAt)
    {
        public static ConsentState Disabled { get; } = new(
            ContractVersions.V1,
            false,
            null,
            null);
    }

    private sealed record EventDocument(
        IReadOnlyList<ResearchEventContract> Events,
        byte[] Bytes);
}

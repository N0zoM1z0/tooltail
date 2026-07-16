using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Application.FileSkills;

public sealed record FileApprenticeStartupResult(
    bool IsReady,
    string ReasonCode,
    bool CreatedCompanion,
    FileSkillWorkspaceStateRecord? Workspace,
    ExecutionRecoveryScanResult? Recovery)
{
    public static FileApprenticeStartupResult Failure(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new(false, reasonCode, false, null, null);
    }
}

public sealed class FileApprenticeStartupService
{
    private const string DefaultPresentationJson =
        "{\"bodyStyle\":\"minimal-apprentice\",\"accent\":\"#5B7CFA\"}";
    private readonly IFileSkillStateStore stateStore;
    private readonly IExecutionJournalReader journalReader;
    private readonly IClock clock;
    private readonly IIdGenerator idGenerator;

    public FileApprenticeStartupService(
        IFileSkillStateStore stateStore,
        IExecutionJournalReader journalReader,
        IClock clock,
        IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(journalReader);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        this.stateStore = stateStore;
        this.journalReader = journalReader;
        this.clock = clock;
        this.idGenerator = idGenerator;
    }

    public async Task<FileApprenticeStartupResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            return FileApprenticeStartupResult.Failure("startup.non_utc_time");
        }

        StateReadResult<IReadOnlyList<CompanionStateRecord>> discovered =
            await stateStore.ListCompanionsAsync(cancellationToken).ConfigureAwait(false);
        if (!discovered.IsSuccess)
        {
            return FileApprenticeStartupResult.Failure(discovered.ReasonCode);
        }

        if (discovered.Value!.Count > 1)
        {
            return FileApprenticeStartupResult.Failure(
                "startup.multiple_companions_require_selection");
        }

        bool created = discovered.Value.Count == 0;
        CompanionId companionId;
        if (created)
        {
            companionId = new CompanionId(idGenerator.NewId());
            StateWriteResult stored = await stateStore.StoreCompanionAsync(
                new CompanionStateRecord(
                    companionId,
                    "Pip",
                    now,
                    IdentitySchemaVersion: 1,
                    DefaultPresentationJson),
                cancellationToken).ConfigureAwait(false);
            if (!stored.IsSuccess)
            {
                return FileApprenticeStartupResult.Failure(stored.FailureCode!);
            }
        }
        else
        {
            companionId = discovered.Value[0].Id;
        }

        StateReadResult<FileSkillWorkspaceStateRecord> workspace =
            await stateStore.LoadWorkspaceStateAsync(
                companionId,
                cancellationToken).ConfigureAwait(false);
        if (!workspace.IsSuccess)
        {
            return FileApprenticeStartupResult.Failure(workspace.ReasonCode);
        }

        ExecutionRecoveryScanResult recovery =
            await journalReader.ScanRecoveryRequiredAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!recovery.IsSuccess)
        {
            return FileApprenticeStartupResult.Failure(recovery.ReasonCode);
        }

        return new FileApprenticeStartupResult(
            true,
            created ? "startup.first_run_ready" : "startup.persisted_state_ready",
            created,
            workspace.Value,
            recovery);
    }
}

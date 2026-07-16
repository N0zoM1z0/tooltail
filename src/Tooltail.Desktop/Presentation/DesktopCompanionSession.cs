using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Desktop.Presentation;

public sealed class DesktopCompanionSession
{
    private readonly object gate = new();
    private CompanionId companionId;

    public DesktopCompanionSession(IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        companionId = new CompanionId(idGenerator.NewId());
    }

    public CompanionId CompanionId
    {
        get
        {
            lock (gate)
            {
                return companionId;
            }
        }
    }

    public void Restore(CompanionId persistedCompanionId)
    {
        if (persistedCompanionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "A desktop companion session requires a durable non-empty identity.",
                nameof(persistedCompanionId));
        }

        lock (gate)
        {
            companionId = persistedCompanionId;
        }
    }
}

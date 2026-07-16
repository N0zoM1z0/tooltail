using Tooltail.Application.Abstractions;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Desktop.Presentation;

public sealed class DesktopCompanionSession
{
    public DesktopCompanionSession(IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(idGenerator);
        CompanionId = new CompanionId(idGenerator.NewId());
    }

    public CompanionId CompanionId { get; }
}

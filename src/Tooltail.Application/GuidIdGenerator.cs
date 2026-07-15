using Tooltail.Application.Abstractions;

namespace Tooltail.Application;

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

using Tooltail.Application.Abstractions;

namespace Tooltail.Application;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

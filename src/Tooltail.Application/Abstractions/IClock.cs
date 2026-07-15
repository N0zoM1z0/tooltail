namespace Tooltail.Application.Abstractions;

/// <summary>
/// Supplies time to application and domain workflows without binding tests to the system clock.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

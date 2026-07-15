namespace Tooltail.Application.Abstractions;

/// <summary>
/// Supplies opaque identifiers without introducing nondeterminism into tests.
/// </summary>
public interface IIdGenerator
{
    Guid NewId();
}

namespace Tooltail.Domain.Identifiers;

public readonly record struct CompanionId
{
    public CompanionId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct LeaseId
{
    public LeaseId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct GrantId
{
    public GrantId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct TeachingEpisodeId
{
    public TeachingEpisodeId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct ExampleId
{
    public ExampleId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct SkillId
{
    public SkillId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct PlanId
{
    public PlanId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct ApprovalId
{
    public ApprovalId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct ExecutionId
{
    public ExecutionId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct ReceiptId
{
    public ReceiptId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

public readonly record struct RunId
{
    public RunId(Guid value) => Value = IdentifierGuard.NotEmpty(value);

    public Guid Value { get; }
}

internal static class IdentifierGuard
{
    public static Guid NotEmpty(Guid value) =>
        value != Guid.Empty
            ? value
            : throw new ArgumentException("Domain identifiers cannot be empty.", nameof(value));
}

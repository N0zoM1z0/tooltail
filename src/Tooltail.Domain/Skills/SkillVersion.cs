using Tooltail.Domain.Common;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Domain.Skills;

public readonly record struct SkillVersionNumber
{
    public SkillVersionNumber(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

        Value = value;
    }

    public int Value { get; }
}

public enum SkillLifecycleState
{
    Draft,
    Approved,
    Practiced,
    Reliable,
    Delegated,
    Stale,
}

public sealed record SkillVersion
{
    public SkillVersion(
        SkillId skillId,
        SkillVersionNumber number,
        SkillVersionNumber? parent,
        string specificationHash,
        string compilerVersion,
        string minimumExecutorVersion,
        SkillLifecycleState lifecycle,
        DateTimeOffset createdAt)
    {
        IdentifierGuard.NotEmpty(skillId.Value);
        ArgumentOutOfRangeException.ThrowIfLessThan(number.Value, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(specificationHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(minimumExecutorVersion);
        if (!Enum.IsDefined(lifecycle))
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycle));
        }

        if (parent is not null && parent.Value.Value >= number.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(parent));
        }

        if (parent is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parent.Value.Value, 1);
        }

        SkillId = skillId;
        Number = number;
        Parent = parent;
        SpecificationHash = specificationHash;
        CompilerVersion = compilerVersion;
        MinimumExecutorVersion = minimumExecutorVersion;
        Lifecycle = lifecycle;
        CreatedAt = createdAt;
    }

    public SkillId SkillId { get; }

    public SkillVersionNumber Number { get; }

    public SkillVersionNumber? Parent { get; }

    public string SpecificationHash { get; }

    public string CompilerVersion { get; }

    public string MinimumExecutorVersion { get; }

    public SkillLifecycleState Lifecycle { get; private init; }

    public DateTimeOffset CreatedAt { get; }

    public static DomainResult<SkillVersion> Rehydrate(
        SkillId skillId,
        SkillVersionNumber number,
        SkillVersionNumber? parent,
        string specificationHash,
        string compilerVersion,
        string minimumExecutorVersion,
        SkillLifecycleState lifecycle,
        DateTimeOffset createdAt,
        bool wasApproved)
    {
        if (!Enum.IsDefined(lifecycle) || createdAt.Offset != TimeSpan.Zero ||
            (lifecycle == SkillLifecycleState.Draft && wasApproved) ||
            (lifecycle is SkillLifecycleState.Approved or
                SkillLifecycleState.Practiced or
                SkillLifecycleState.Reliable or
                SkillLifecycleState.Delegated && !wasApproved))
        {
            return DomainResult.Failure<SkillVersion>(
                "skill.rehydrate_state_invalid",
                "Persisted skill lifecycle history is inconsistent.");
        }

        SkillVersion current = new(
            skillId,
            number,
            parent,
            specificationHash,
            compilerVersion,
            minimumExecutorVersion,
            SkillLifecycleState.Draft,
            createdAt);
        SkillLifecycleState[] transitions = lifecycle switch
        {
            SkillLifecycleState.Draft => [],
            SkillLifecycleState.Approved => [SkillLifecycleState.Approved],
            SkillLifecycleState.Practiced =>
                [SkillLifecycleState.Approved, SkillLifecycleState.Practiced],
            SkillLifecycleState.Reliable =>
                [
                    SkillLifecycleState.Approved,
                    SkillLifecycleState.Practiced,
                    SkillLifecycleState.Reliable,
                ],
            SkillLifecycleState.Delegated =>
                [
                    SkillLifecycleState.Approved,
                    SkillLifecycleState.Practiced,
                    SkillLifecycleState.Reliable,
                    SkillLifecycleState.Delegated,
                ],
            SkillLifecycleState.Stale when wasApproved =>
                [SkillLifecycleState.Approved, SkillLifecycleState.Stale],
            SkillLifecycleState.Stale => [SkillLifecycleState.Stale],
            _ => [],
        };
        foreach (SkillLifecycleState transition in transitions)
        {
            DomainResult<SkillVersion> transitioned = current.TransitionTo(transition);
            if (!transitioned.IsSuccess)
            {
                return DomainResult.Failure<SkillVersion>(
                    "skill.rehydrate_transition_invalid",
                    "Persisted skill lifecycle cannot be replayed safely.");
            }

            current = transitioned.Value!;
        }

        return DomainResult.Success(current);
    }

    public DomainResult<SkillVersion> TransitionTo(SkillLifecycleState next)
    {
        if (!Enum.IsDefined(next))
        {
            return DomainResult.Failure<SkillVersion>(
                "skill.lifecycle_unknown",
                "The requested skill lifecycle is unknown.");
        }

        bool allowed = (Lifecycle, next) switch
        {
            (SkillLifecycleState.Draft, SkillLifecycleState.Approved) => true,
            (SkillLifecycleState.Approved, SkillLifecycleState.Practiced) => true,
            (SkillLifecycleState.Practiced, SkillLifecycleState.Reliable) => true,
            (SkillLifecycleState.Reliable, SkillLifecycleState.Delegated) => true,
            (_, SkillLifecycleState.Stale) when Lifecycle != SkillLifecycleState.Stale => true,
            _ => false,
        };

        return allowed
            ? DomainResult.Success(this with { Lifecycle = next })
            : DomainResult.Failure<SkillVersion>(
                "skill.lifecycle_invalid_transition",
                $"Skill lifecycle '{Lifecycle}' cannot transition to '{next}'.");
    }
}

public sealed record Skill
{
    public Skill(
        SkillId id,
        CompanionId companionId,
        string displayName,
        SkillVersion currentVersion,
        DateTimeOffset createdAt)
    {
        IdentifierGuard.NotEmpty(id.Value);
        IdentifierGuard.NotEmpty(companionId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (currentVersion.SkillId != id)
        {
            throw new ArgumentException("The current version must belong to the skill.", nameof(currentVersion));
        }

        Id = id;
        CompanionId = companionId;
        DisplayName = displayName;
        CurrentVersion = currentVersion;
        CreatedAt = createdAt;
    }

    public SkillId Id { get; }

    public CompanionId CompanionId { get; }

    public string DisplayName { get; }

    public SkillVersion CurrentVersion { get; }

    public DateTimeOffset CreatedAt { get; }
}

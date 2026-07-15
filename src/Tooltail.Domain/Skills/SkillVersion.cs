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
        ArgumentException.ThrowIfNullOrWhiteSpace(specificationHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(minimumExecutorVersion);
        if (parent is not null && parent.Value.Value >= number.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(parent));
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

    public DomainResult<SkillVersion> TransitionTo(SkillLifecycleState next)
    {
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

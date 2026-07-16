using Tooltail.Contracts.Json;

namespace Tooltail.Contracts.Research;

internal static class ResearchEventContractValidator
{
    internal static ContractParseResult<ResearchEventContract> Validate(
        ResearchEventContract value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool valid = value.EventId != Guid.Empty &&
            value.StudyId != Guid.Empty &&
            value.SessionId != Guid.Empty &&
            value.Sequence is >= 0 and <= 100_000 &&
            value.OccurredAt.Offset == TimeSpan.Zero &&
            Enum.IsDefined(value.Type) &&
            IsReasonCode(value.ReasonCode) &&
            value.DurationMilliseconds is null or >= 0 and <= 86_400_000 &&
            value.Count is null or >= 0 and <= 10_000 &&
            value.SkillVersion is null or >= 1 and <= 10_000 &&
            (value.BodyState is null || Enum.IsDefined(value.BodyState.Value)) &&
            (value.PathToken is null || IsLowerHex(value.PathToken)) &&
            value.Rating is null or >= 1 and <= 7;
        return valid
            ? ContractParseResult.Success(value)
            : ContractParseResult.Failure<ResearchEventContract>(
                "contract.invalid_research_event",
                "The research event contains invalid or unbounded minimized data.");
    }

    private static bool IsReasonCode(string value) =>
        value.Length is >= 1 and <= 64 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '_' or '.' or '-');

    private static bool IsLowerHex(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

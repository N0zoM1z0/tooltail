using System.IO;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Research;
using Tooltail.Infrastructure.LocalResearch;

namespace Tooltail.Desktop.Presentation;

public sealed class ResearchEventRecorder(
    LocalResearchStore store,
    ResearchStudyViewModel viewModel,
    IClock clock)
{
    public DateTimeOffset StartTiming() => clock.UtcNow;

    public async Task RecordAsync(
        ResearchEventType type,
        DateTimeOffset startedUtc,
        bool success,
        string reasonCode,
        int? count = null,
        int? skillVersion = null,
        ResearchBodyState? bodyState = null,
        string? pathToken = null,
        int? rating = null)
    {
        if (!store.IsEnabled)
        {
            return;
        }

        try
        {
            DateTimeOffset completedUtc = clock.UtcNow;
            long duration = Math.Clamp(
                (long)Math.Max(0, (completedUtc - startedUtc).TotalMilliseconds),
                0,
                86_400_000);
            ResearchWriteResult result = await store.RecordAsync(
                new ResearchEventInput(
                    type,
                    success,
                    reasonCode,
                    DurationMilliseconds: duration,
                    Count: count,
                    SkillVersion: skillVersion,
                    BodyState: bodyState,
                    PathToken: pathToken,
                    Rating: rating)).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                viewModel.ApplyStatus(await store.InitializeAsync().ConfigureAwait(true));
            }
            else
            {
                viewModel.ApplyWrite(result);
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ObjectDisposedException)
        {
            // Research is observational only. A local research failure cannot alter
            // or replace the product workflow result that was already committed.
        }
    }
}

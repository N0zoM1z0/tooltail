using System.Text;
using Tooltail.Application.Abstractions;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Research;
using Tooltail.Infrastructure.LocalResearch;

namespace Tooltail.Infrastructure.LocalResearch.Tests;

public sealed class LocalResearchStoreTests
{
    [Fact]
    public async Task DefaultInitializationCreatesNoResearchSink()
    {
        using TestRoot root = new();
        await using LocalResearchStore store = root.CreateStore();

        ResearchStoreStatus status = await store.InitializeAsync();
        ResearchWriteResult write = await store.RecordAsync(
            new ResearchEventInput(
                ResearchEventType.LessonCompleted,
                true,
                "lesson.complete"));

        Assert.True(status.IsSuccess);
        Assert.False(status.IsEnabled);
        Assert.Equal("research.off_by_default", status.ReasonCode);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "Research")));
        Assert.Equal("research.consent_required", write.ReasonCode);
    }

    [Fact]
    public async Task ExplicitOptInRecordsOnlyClosedValidatedEventsAndSaltedTokens()
    {
        using TestRoot root = new();
        await using LocalResearchStore store = root.CreateStore();
        await store.InitializeAsync();
        ResearchStoreStatus enabled = await store.EnableAsync();
        ResearchTokenResult token = store.TokenizeRelativePath("Invoices\\invoice-edge.pdf");
        ResearchWriteResult written = await store.RecordAsync(
            new ResearchEventInput(
                ResearchEventType.RehearsalCompleted,
                true,
                "rehearsal.production_plan_ready",
                DurationMilliseconds: 842,
                Count: 1,
                SkillVersion: 2,
                BodyState: ResearchBodyState.NeedsInput,
                PathToken: token.Token));
        ResearchPreviewResult preview = await store.PreviewAsync();

        Assert.True(enabled.IsEnabled);
        Assert.NotEqual(Guid.Empty, enabled.StudyId);
        Assert.NotEqual(Guid.Empty, enabled.SessionId);
        Assert.True(token.IsSuccess);
        Assert.Matches("^[0-9a-f]{64}$", token.Token!);
        Assert.True(written.IsSuccess, written.ReasonCode);
        Assert.Equal(3, preview.EventCount);
        Assert.DoesNotContain("Invoices", preview.PreviewJsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice-edge.pdf", preview.PreviewJsonl, StringComparison.Ordinal);
        Assert.All(
            preview.PreviewJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(ContractJson.ParseResearchEvent(
                Encoding.UTF8.GetBytes(line)).IsSuccess));
    }

    [Fact]
    public async Task PreviewAndCreateNewExportRoundTripWithoutUploadSurface()
    {
        using TestRoot root = new();
        await using LocalResearchStore store = root.CreateStore();
        await store.InitializeAsync();
        await store.EnableAsync();
        await store.RecordAsync(
            new ResearchEventInput(
                ResearchEventType.CapsuleExported,
                true,
                "capsule.exported",
                Count: 2));

        ResearchPreviewResult preview = await store.PreviewAsync();
        ResearchExportResult export = await store.ExportAsync();

        Assert.True(preview.IsSuccess);
        Assert.True(export.IsSuccess, export.ReasonCode);
        Assert.NotNull(export.CanonicalPath);
        Assert.Equal(preview.ByteCount, export.ByteCount);
        Assert.Equal(
            preview.PreviewJsonl,
            await File.ReadAllTextAsync(export.CanonicalPath!));
        Assert.DoesNotContain(
            "HttpClient",
            File.ReadAllText(SourcePath("LocalResearchStore.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartKeepsStudyButStartsNewSessionAndSalt()
    {
        using TestRoot root = new();
        Guid? study;
        Guid? firstSession;
        string? firstToken;
        await using (LocalResearchStore first = root.CreateStore())
        {
            await first.InitializeAsync();
            ResearchStoreStatus enabled = await first.EnableAsync();
            study = enabled.StudyId;
            firstSession = enabled.SessionId;
            firstToken = first.TokenizeRelativePath("Invoices\\edge.pdf").Token;
        }

        await using LocalResearchStore second = root.CreateStore();
        ResearchStoreStatus restarted = await second.InitializeAsync();
        string? secondToken = second.TokenizeRelativePath("Invoices\\edge.pdf").Token;

        Assert.True(restarted.IsEnabled);
        Assert.Equal(study, restarted.StudyId);
        Assert.NotEqual(firstSession, restarted.SessionId);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(3, restarted.EventCount);
    }

    [Fact]
    public async Task OneClickDeletionTruncatesOnlyReviewedArtifactsAndDisablesConsent()
    {
        using TestRoot root = new();
        await using LocalResearchStore store = root.CreateStore();
        await store.InitializeAsync();
        await store.EnableAsync();
        await store.RecordAsync(
            new ResearchEventInput(
                ResearchEventType.ExecutionCompleted,
                true,
                "execution.production_verified"));
        ResearchExportResult export = await store.ExportAsync();

        ResearchStoreStatus deleted = await store.DeleteAllAsync();
        ResearchPreviewResult preview = await store.PreviewAsync();

        Assert.True(deleted.IsSuccess);
        Assert.False(deleted.IsEnabled);
        Assert.Equal("research.deleted_and_disabled", deleted.ReasonCode);
        Assert.Equal(0, preview.EventCount);
        Assert.Equal(0, new FileInfo(export.CanonicalPath!).Length);
        Assert.False(store.TokenizeRelativePath("Invoices\\edge.pdf").IsSuccess);
        Assert.DoesNotContain(
            "File.Delete(",
            File.ReadAllText(SourcePath("LocalResearchStore.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExportFileFailsDeletionBeforeAnyResearchDataIsTruncated()
    {
        using TestRoot root = new();
        await using LocalResearchStore store = root.CreateStore();
        await store.InitializeAsync();
        await store.EnableAsync();
        ResearchExportResult export = await store.ExportAsync();
        string unexpected = Path.Combine(
            root.Path,
            "Research",
            "Exports",
            "unrelated.txt");
        await File.WriteAllTextAsync(unexpected, "keep");
        long eventBytes = new FileInfo(Path.Combine(
            root.Path,
            "Research",
            "events.jsonl")).Length;

        ResearchStoreStatus result = await store.DeleteAllAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("research.local_state_invalid", result.ReasonCode);
        Assert.Equal(eventBytes, new FileInfo(Path.Combine(
            root.Path,
            "Research",
            "events.jsonl")).Length);
        Assert.True(new FileInfo(export.CanonicalPath!).Length > 0);
        Assert.Equal("keep", await File.ReadAllTextAsync(unexpected));
    }

    [Fact]
    public async Task InvalidEventCannotAppendAndIncompleteTailFailsClosed()
    {
        using TestRoot root = new();
        await using LocalResearchStore store = root.CreateStore();
        await store.InitializeAsync();
        await store.EnableAsync();
        ResearchPreviewResult before = await store.PreviewAsync();

        ResearchWriteResult invalid = await store.RecordAsync(
            new ResearchEventInput(
                ResearchEventType.RatingSubmitted,
                true,
                "Rating Has Spaces",
                Rating: 8));
        ResearchPreviewResult unchanged = await store.PreviewAsync();
        await File.AppendAllTextAsync(
            Path.Combine(root.Path, "Research", "events.jsonl"),
            "{\"incomplete\":true}");
        ResearchPreviewResult corrupt = await store.PreviewAsync();

        Assert.False(invalid.IsSuccess);
        Assert.Equal("contract.invalid_research_event", invalid.ReasonCode);
        Assert.Equal(before.ByteCount, unchanged.ByteCount);
        Assert.False(corrupt.IsSuccess);
        Assert.Equal("research.local_state_invalid", corrupt.ReasonCode);
    }

    [Fact]
    public async Task ReparseResearchRootIsRejectedWhenHostCanCreateIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestRoot root = new();
        string outside = Path.Combine(Path.GetTempPath(), $"tooltail-research-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root.Path, "Research"), outside);
            await using LocalResearchStore store = root.CreateStore();
            ResearchStoreStatus status = await store.InitializeAsync();
            Assert.False(status.IsSuccess);
            Assert.Equal("research.local_state_invalid", status.ReasonCode);
        }
        finally
        {
            Directory.Delete(Path.Combine(root.Path, "Research"));
            Directory.Delete(outside);
        }
    }

    private static string SourcePath(string fileName) => Path.Combine(
        RepositoryRoot(),
        "src",
        "Tooltail.Infrastructure.LocalResearch",
        fileName);

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Tooltail.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException();
    }

    private sealed class TestRoot : IDisposable
    {
        private readonly SequenceIdGenerator ids = new();

        public TestRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tooltail-research-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public LocalResearchStore CreateStore() => new(
            new LocalResearchOptions(Path),
            new FixedClock(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)),
            ids);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private long value;

        public Guid NewId()
        {
            long next = Interlocked.Increment(ref value);
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes[8..], next);
            bytes[7] = 0x40;
            return new Guid(bytes);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}

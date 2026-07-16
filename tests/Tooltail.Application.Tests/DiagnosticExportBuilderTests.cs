using System.Text;
using System.Text.Json.Nodes;
using Tooltail.Application.Abstractions;
using Tooltail.Application.Diagnostics;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Application.Tests;

public sealed class DiagnosticExportBuilderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactPreviewContainsOnlyClosedCountsStateAndReasonCodes()
    {
        FileSkillWorkspaceStateRecord workspace = Workspace(
            displayName: "private companion name",
            presentationJson: "{\"secretPath\":\"C:\\\\private\\\\file.txt\"}");
        CompanionBodyProjection body = new(
            CompanionBodyState.NeedsInput,
            ToolKind: null,
            ParallelUnitCount: 0,
            "body.needs_input");

        DiagnosticEncodingResult result = DiagnosticExportBuilder.Build(
            workspace,
            ExecutionRecoveryScanResult.Success([]),
            body,
            new DiagnosticResearchSnapshot(
                IsEnabled: false,
                EventCount: 0,
                EventBytes: 0,
                "research.off_by_default"),
            Now,
            "0.1.0");

        Assert.True(result.IsSuccess, result.ReasonCode);
        Assert.Equal("diagnostic.preview_ready", result.ReasonCode);
        Assert.InRange(result.Bytes!.Length, 2, DiagnosticExportBuilder.MaximumBytes);
        string json = Encoding.UTF8.GetString(result.Bytes);
        Assert.DoesNotContain("private companion name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secretPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("companionId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private\\file.txt", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Document!.ContentPolicy.ContainsRawPaths);
        Assert.False(result.Document.ContentPolicy.ContainsFileNames);
        Assert.Equal(CompanionBodyState.NeedsInput, result.Document.Body.State);
    }

    [Fact]
    public void UnknownFieldsUnsafePolicyInvalidReasonAndMalformedClosedValuesFailClosed()
    {
        DiagnosticEncodingResult valid = DiagnosticExportBuilder.Build(
            Workspace("Pip", "{}"),
            ExecutionRecoveryScanResult.Success([]),
            new CompanionBodyProjection(
                CompanionBodyState.HomeIdle,
                ToolKind: null,
                ParallelUnitCount: 0,
                "body.home_idle"),
            new DiagnosticResearchSnapshot(false, 0, 0, "research.off_by_default"),
            Now,
            "0.1.0");
        string json = Encoding.UTF8.GetString(valid.Bytes!);
        string unknown = json.Insert(json.IndexOf('{') + 1, "\"unknown\":true,");
        string unsafePolicy = json.Replace(
            "\"containsRawPaths\": false",
            "\"containsRawPaths\": true",
            StringComparison.Ordinal);
        string integerEnum = json.Replace(
            "\"state\": \"home_idle\"",
            "\"state\": 0",
            StringComparison.Ordinal);
        JsonObject nullBodyDocument = JsonNode.Parse(json)!.AsObject();
        nullBodyDocument["body"] = null;
        string nullBody = nullBodyDocument.ToJsonString();
        DiagnosticEncodingResult invalidReason = DiagnosticExportBuilder.Build(
            Workspace("Pip", "{}"),
            ExecutionRecoveryScanResult.Success([]),
            new CompanionBodyProjection(
                CompanionBodyState.HomeIdle,
                ToolKind: null,
                ParallelUnitCount: 0,
                "raw path C:\\private"),
            new DiagnosticResearchSnapshot(false, 0, 0, "research.off_by_default"),
            Now,
            "0.1.0");

        Assert.False(DiagnosticExportBuilder.Parse(Encoding.UTF8.GetBytes(unknown)).IsSuccess);
        Assert.False(
            DiagnosticExportBuilder.Parse(Encoding.UTF8.GetBytes(unsafePolicy)).IsSuccess);
        Assert.False(
            DiagnosticExportBuilder.Parse(Encoding.UTF8.GetBytes(integerEnum)).IsSuccess);
        Assert.False(
            DiagnosticExportBuilder.Parse(Encoding.UTF8.GetBytes(nullBody)).IsSuccess);
        Assert.False(invalidReason.IsSuccess);
        Assert.Equal("diagnostic.input_invalid", invalidReason.ReasonCode);
    }

    private static FileSkillWorkspaceStateRecord Workspace(
        string displayName,
        string presentationJson) =>
        new(
            new CompanionStateRecord(
                new CompanionId(
                    Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")),
                displayName,
                Now.AddDays(-1),
                IdentitySchemaVersion: 1,
                presentationJson),
            Grants: [],
            CurrentSkills: [],
            TeachingEpisodes: [],
            Executions: []);
}

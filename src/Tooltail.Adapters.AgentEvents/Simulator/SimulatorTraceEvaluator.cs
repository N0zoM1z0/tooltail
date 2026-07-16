using Tooltail.Adapters.AgentEvents.Streaming;
using Tooltail.Contracts.Json;
using Tooltail.Domain.Agents;

namespace Tooltail.Adapters.AgentEvents.Simulator;

public static class SimulatorTraceEvaluator
{
    public static async Task<SimulatorTraceVerification> VerifyAsync(
        SimulatorTraceDefinition trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        await using Stream input = trace.OpenRead();
        AgentEventStreamResult actual = await NormalizedAgentJsonlAdapter.ReadAsync(
            input,
            NormalizedAgentEventSource.Simulator,
            trace.Limits,
            cancellationToken).ConfigureAwait(false);
        List<string> mismatches = [];
        SimulatorTraceExpectation expected = trace.Expectation;
        if (actual.Status != expected.Status)
        {
            mismatches.Add("simulator_trace.status_mismatch");
        }

        if (!StringComparer.Ordinal.Equals(actual.ReasonCode, expected.ReasonCode))
        {
            mismatches.Add("simulator_trace.reason_mismatch");
        }

        if (actual.AcceptedEventCount != expected.AcceptedEventCount)
        {
            mismatches.Add("simulator_trace.accepted_count_mismatch");
        }

        if (actual.DuplicateEventCount != expected.DuplicateEventCount)
        {
            mismatches.Add("simulator_trace.duplicate_count_mismatch");
        }

        if (!actual.Steps.Select(static step => step.Body).SequenceEqual(expected.Bodies))
        {
            mismatches.Add("simulator_trace.body_sequence_mismatch");
        }

        return new SimulatorTraceVerification(trace, actual, mismatches.AsReadOnly());
    }

    public static async Task<SimulatorTraceCatalogReport> CreateCatalogReportAsync(
        CancellationToken cancellationToken = default)
    {
        List<SimulatorTraceReport> reports = [];
        foreach (SimulatorTraceDefinition trace in SimulatorTraceCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SimulatorTraceVerification verification = await VerifyAsync(
                trace,
                cancellationToken).ConfigureAwait(false);
            reports.Add(ToReport(verification));
        }

        return new SimulatorTraceCatalogReport(
            ContractVersions.V1,
            reports.All(static report => report.MatchesExpected),
            reports.AsReadOnly());
    }

    public static SimulatorTraceReport ToReport(SimulatorTraceVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        AgentEventStreamResult actual = verification.Actual;
        return new SimulatorTraceReport(
            verification.Trace.Name,
            verification.Trace.Description,
            verification.Trace.JsonlByteCount,
            verification.IsMatch,
            actual.Status,
            actual.ReasonCode,
            actual.InputLineCount,
            actual.InputByteCount,
            actual.AcceptedEventCount,
            actual.DuplicateEventCount,
            actual.RunId?.Value,
            verification.MismatchCodes,
            actual.Steps
                .Select(static step => new SimulatorTraceStepReport(
                    step.InputLine,
                    step.Sequence,
                    step.EventType,
                    step.Disposition,
                    step.Body.State,
                    step.Body.ToolKind,
                    step.Body.ParallelUnitCount,
                    step.Body.ReasonCode))
                .ToArray());
    }
}

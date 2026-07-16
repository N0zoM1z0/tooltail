using System.Globalization;
using System.Text;

namespace Tooltail.Adapters.AgentEvents.Simulator;

public static class SimulatorTraceGoldenFormatter
{
    public static string Format(SimulatorTraceCatalogReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var output = new StringBuilder("tooltail-simulator-golden/1\n");
        foreach (SimulatorTraceReport trace in report.Traces)
        {
            output.Append(trace.Trace);
            Append(output, trace.Status);
            Append(output, trace.ReasonCode);
            Append(output, trace.JsonlByteCount);
            Append(output, trace.InputLineCount);
            Append(output, trace.InputByteCount);
            Append(output, trace.AcceptedEventCount);
            Append(output, trace.DuplicateEventCount);
            output.Append('\t');
            if (trace.Steps.Count == 0)
            {
                output.Append('-');
            }

            for (int index = 0; index < trace.Steps.Count; index++)
            {
                if (index > 0)
                {
                    output.Append('>');
                }

                SimulatorTraceStepReport step = trace.Steps[index];
                output.Append(step.InputLine.ToString(CultureInfo.InvariantCulture));
                output.Append('/');
                output.Append(step.Sequence.ToString(CultureInfo.InvariantCulture));
                output.Append('/');
                output.Append(step.EventType);
                output.Append('/');
                output.Append(step.Disposition);
                output.Append('/');
                output.Append(step.BodyState);
                output.Append('/');
                output.Append(step.ToolKind?.ToString() ?? "-");
                output.Append('/');
                output.Append(step.ParallelUnitCount.ToString(CultureInfo.InvariantCulture));
                output.Append('/');
                output.Append(step.BodyReasonCode);
            }

            output.Append('\n');
        }

        return output.ToString();
    }

    private static void Append(StringBuilder output, object value)
    {
        output.Append('\t');
        output.AppendFormat(CultureInfo.InvariantCulture, "{0}", value);
    }
}

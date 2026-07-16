using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tooltail.Domain.Agents;
using Tooltail.Domain.Identifiers;

namespace Tooltail.Adapters.AgentEvents.Codex;

public sealed class CodexRawEventNormalizer
{
    private const int MaximumRawTypeCharacters = 64;
    private const int MaximumRawItemIdCharacters = 256;
    private readonly Dictionary<string, NormalizedAgentToolKind> activeItems =
        new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private long sequence;
    private DateTimeOffset? lastOccurredUtc;
    private bool started;
    private bool terminal;
    private NormalizedAgentEventType? terminalType;

    public CodexRawEventNormalizer(RunId runId, TimeProvider? timeProvider = null)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Run identity cannot be empty.", nameof(runId));
        }

        RunId = runId;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RunId RunId { get; }

    public bool IsTerminal => terminal;

    public CodexRawEventMapResult Map(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetBoundedString(
                    root,
                    "type",
                    MaximumRawTypeCharacters,
                    out string? rawType))
            {
                return Rejected("codex_adapter.event_shape_invalid");
            }

            return rawType switch
            {
                "thread.started" => StartRunIfNeeded(),
                "turn.started" => StartRunIfNeeded(),
                "turn.completed" => CompleteRun(),
                "turn.failed" => FailRun(),
                "error" => Disconnect("provider_error"),
                "item.started" => MapItem(root, startedEvent: true),
                "item.completed" => MapItem(root, startedEvent: false),
                "item.updated" => IgnoredKnown("codex_adapter.item_update_ignored"),
                _ => IgnoredUnknown(),
            };
        }
        catch (JsonException)
        {
            return Rejected("codex_adapter.invalid_json");
        }
    }

    public NormalizedAgentEvent? CreateCancellationEvent()
    {
        if (terminal)
        {
            return null;
        }

        terminal = true;
        terminalType = started
            ? NormalizedAgentEventType.RunCancelled
            : NormalizedAgentEventType.AdapterDisconnected;
        return started
            ? Emit(
                NormalizedAgentEventType.RunCancelled,
                NormalizedAgentEventSeverity.Warning,
                statusCode: "adapter_cancelled")
            : Emit(
                NormalizedAgentEventType.AdapterDisconnected,
                NormalizedAgentEventSeverity.Warning,
                statusCode: "cancelled_before_start");
    }

    public NormalizedAgentEvent? CreateDisconnectEvent(string statusCode)
    {
        if (terminal)
        {
            return null;
        }

        terminal = true;
        terminalType = NormalizedAgentEventType.AdapterDisconnected;
        return Emit(
            NormalizedAgentEventType.AdapterDisconnected,
            NormalizedAgentEventSeverity.Error,
            statusCode: statusCode);
    }

    private CodexRawEventMapResult StartRunIfNeeded()
    {
        if (terminal)
        {
            return Rejected("codex_adapter.event_after_terminal");
        }

        if (started)
        {
            return IgnoredKnown("codex_adapter.start_already_observed");
        }

        started = true;
        return Emitted(
            Emit(
                NormalizedAgentEventType.RunStarted,
                NormalizedAgentEventSeverity.Info,
                statusCode: "codex_exec_started"));
    }

    private CodexRawEventMapResult CompleteRun()
    {
        if (!started)
        {
            return Rejected("codex_adapter.run_not_started");
        }

        if (terminal)
        {
            return Rejected("codex_adapter.event_after_terminal");
        }

        if (activeItems.Count > 0)
        {
            return Rejected("codex_adapter.active_items_at_completion");
        }

        terminal = true;
        terminalType = NormalizedAgentEventType.RunCompleted;
        return Emitted(
            Emit(
                NormalizedAgentEventType.RunCompleted,
                NormalizedAgentEventSeverity.Info,
                statusCode: "codex_exec_completed"));
    }

    private CodexRawEventMapResult FailRun()
    {
        if (!started)
        {
            return Rejected("codex_adapter.run_not_started");
        }

        if (terminal)
        {
            return terminalType is NormalizedAgentEventType.ToolFailed or
                NormalizedAgentEventType.RunFailed
                ? IgnoredKnown("codex_adapter.failure_already_observed")
                : Rejected("codex_adapter.event_after_terminal");
        }

        terminal = true;
        terminalType = NormalizedAgentEventType.RunFailed;
        return Emitted(
            Emit(
                NormalizedAgentEventType.RunFailed,
                NormalizedAgentEventSeverity.Error,
                statusCode: "codex_exec_failed"));
    }

    private CodexRawEventMapResult Disconnect(string statusCode)
    {
        if (terminal)
        {
            return IgnoredKnown("codex_adapter.terminal_error_ignored");
        }

        terminal = true;
        terminalType = NormalizedAgentEventType.AdapterDisconnected;
        return Emitted(
            Emit(
                NormalizedAgentEventType.AdapterDisconnected,
                NormalizedAgentEventSeverity.Error,
                statusCode: statusCode),
            "codex_adapter.provider_error");
    }

    private CodexRawEventMapResult MapItem(JsonElement root, bool startedEvent)
    {
        if (!started)
        {
            return Rejected("codex_adapter.run_not_started");
        }

        if (terminal)
        {
            return Rejected("codex_adapter.event_after_terminal");
        }

        if (!root.TryGetProperty("item", out JsonElement item) ||
            item.ValueKind != JsonValueKind.Object ||
            !TryGetBoundedString(
                item,
                "type",
                MaximumRawTypeCharacters,
                out string? itemType))
        {
            return Rejected("codex_adapter.item_shape_invalid");
        }

        NormalizedAgentToolKind? toolKind = itemType switch
        {
            "command_execution" => NormalizedAgentToolKind.Terminal,
            "file_change" => NormalizedAgentToolKind.File,
            "mcp_tool_call" => NormalizedAgentToolKind.Other,
            "web_search" => NormalizedAgentToolKind.Browser,
            "agent_message" or "reasoning" or "plan_update" => null,
            _ => null,
        };
        if (toolKind is null)
        {
            return itemType is "agent_message" or "reasoning" or "plan_update"
                ? IgnoredKnown("codex_adapter.content_item_discarded")
                : IgnoredUnknown();
        }

        if (!TryGetBoundedString(
                item,
                "id",
                MaximumRawItemIdCharacters,
                out string? rawItemId))
        {
            return Rejected("codex_adapter.item_id_invalid");
        }

        string itemId = HashItemId(rawItemId);
        if (startedEvent)
        {
            if (!activeItems.TryAdd(itemId, toolKind.Value))
            {
                return Rejected("codex_adapter.item_already_active");
            }

            return Emitted(
                EmitTool(
                    NormalizedAgentEventType.ToolStarted,
                    NormalizedAgentEventSeverity.Info,
                    toolKind.Value,
                    itemId,
                    "codex_item_started"));
        }

        if (!activeItems.TryGetValue(itemId, out NormalizedAgentToolKind activeKind) ||
            activeKind != toolKind.Value)
        {
            return Rejected("codex_adapter.item_not_active");
        }

        if (!TryGetOptionalStatus(item, out string? status))
        {
            return Rejected("codex_adapter.item_status_invalid");
        }

        bool failed = StringComparer.Ordinal.Equals(status, "failed");
        if (status is not null &&
            !failed &&
            !StringComparer.Ordinal.Equals(status, "completed"))
        {
            return Rejected("codex_adapter.item_status_unknown");
        }

        activeItems.Remove(itemId);
        if (failed)
        {
            terminal = true;
            terminalType = NormalizedAgentEventType.ToolFailed;
        }

        return Emitted(
            EmitTool(
                failed
                    ? NormalizedAgentEventType.ToolFailed
                    : NormalizedAgentEventType.ToolCompleted,
                failed
                    ? NormalizedAgentEventSeverity.Error
                    : NormalizedAgentEventSeverity.Info,
                toolKind.Value,
                itemId,
                failed ? "codex_item_failed" : "codex_item_completed"));
    }

    private NormalizedAgentEvent EmitTool(
        NormalizedAgentEventType type,
        NormalizedAgentEventSeverity severity,
        NormalizedAgentToolKind toolKind,
        string toolCallId,
        string statusCode) =>
        Emit(type, severity, toolKind, toolCallId, statusCode);

    private NormalizedAgentEvent Emit(
        NormalizedAgentEventType type,
        NormalizedAgentEventSeverity severity,
        NormalizedAgentToolKind? toolKind = null,
        string? toolCallId = null,
        string? statusCode = null)
    {
        DateTimeOffset occurredUtc = timeProvider.GetUtcNow().ToUniversalTime();
        if (lastOccurredUtc is not null && occurredUtc < lastOccurredUtc)
        {
            occurredUtc = lastOccurredUtc.Value;
        }

        lastOccurredUtc = occurredUtc;
        var data = NormalizedAgentEventData.Create(
            toolKind: toolKind,
            toolCallId: toolCallId,
            statusCode: statusCode).Value!;
        return NormalizedAgentEvent.Create(
            new AgentEventId(Guid.NewGuid()),
            RunId,
            sequence++,
            occurredUtc,
            NormalizedAgentEventSource.CodexExecJsonl,
            type,
            severity,
            data).Value!;
    }

    private static bool TryGetBoundedString(
        JsonElement container,
        string propertyName,
        int maximumCharacters,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!container.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null &&
            value.Length is > 0 &&
            value.Length <= maximumCharacters &&
            !value.Any(char.IsControl);
    }

    private static bool TryGetOptionalStatus(JsonElement item, out string? status)
    {
        status = null;
        if (!item.TryGetProperty("status", out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        status = element.GetString();
        return status is not null &&
            status.Length is > 0 and <= MaximumRawTypeCharacters &&
            !status.Any(char.IsControl);
    }

    private static string HashItemId(string rawItemId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawItemId));
        return $"codex-item-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static CodexRawEventMapResult Emitted(
        NormalizedAgentEvent agentEvent,
        string reasonCode = "codex_adapter.event_emitted") =>
        new(CodexRawEventDisposition.Emitted, reasonCode, agentEvent);

    private static CodexRawEventMapResult IgnoredKnown(string reasonCode) =>
        new(CodexRawEventDisposition.IgnoredKnown, reasonCode, Event: null);

    private static CodexRawEventMapResult IgnoredUnknown() =>
        new(
            CodexRawEventDisposition.IgnoredUnknown,
            "codex_adapter.unknown_event_ignored",
            Event: null);

    private static CodexRawEventMapResult Rejected(string reasonCode) =>
        new(CodexRawEventDisposition.Rejected, reasonCode, Event: null);
}

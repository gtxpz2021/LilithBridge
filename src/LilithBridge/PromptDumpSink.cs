using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

/// <summary>
/// Test-only full prompt dump. Default disabled.
/// Writes exact request payload only when BridgeConfig.PromptDumpEnabled = true.
/// </summary>
internal static class PromptDumpSink
{
    private static int _sequence;
    private static readonly object MaterialSelectionLock = new();
    private static string _materialSelectionJson = "";
    private static string _materialSelectionContext = "";
    private static DateTimeOffset _materialSelectionAt = DateTimeOffset.MinValue;

    public static void Write(
        BridgeConfig config,
        string requestKind,
        string payloadJson,
        IReadOnlyList<string>? runtimeBlocks = null,
        string? requestPurpose = null,
        string? requestId = null,
        string? attemptId = null,
        HistoryProjectionSummary? historyProjection = null,
        string? historyBoundary = null)
    {
        if (config == null
            || !config.PromptDumpEnabled
            || string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        try
        {
            string root = string.IsNullOrWhiteSpace(config.DataDirectory)
                ? DataPaths.Root()
                : config.DataDirectory;
            string dir = Path.Combine(root, "telemetry", "prompt_dumps");
            Directory.CreateDirectory(dir);

            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
                + "-" + Interlocked.Increment(ref _sequence).ToString("D4")
                + "-" + Guid.NewGuid().ToString("N")[..6];

            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            // The actual chat request carries the material selection that built
            // its context. It is matched by context identity, so an auxiliary
            // request cannot inherit a chat selection by timing alone.
            JsonElement? materialSelection = requestKind == "main" ? LatestMaterialSelection(doc.RootElement) : null;
            var record = new
            {
                request_kind = requestKind,
                request_purpose = requestPurpose ?? requestKind,
                // Which ownership mode the final request-history boundary used:
                // bound / unbound / none. Diagnostic only; it changes no payload.
                history_boundary = historyBoundary ?? RequestHistoryOwnership.None,
                request_id = requestId,
                attempt_id = attemptId,
                model = doc.RootElement.GetProperty("model").GetString(),
                timestamp = DateTimeOffset.Now,
                runtime_blocks = runtimeBlocks ?? Array.Empty<string>(),
                material_selection = materialSelection,
                // Describes the exact outgoing payload, including history and
                // image blocks. Character counts are not provider token usage.
                payload_summary = DescribePayload(doc.RootElement, historyProjection),
                payload = doc.RootElement
            };

            File.WriteAllText(
                Path.Combine(dir, id + ".json"),
                JsonSerializer.Serialize(
                    record,
                    new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(dir, id + ".txt"),
                JsonSerializer.Serialize(
                    doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostic dumping must never break the request path.
        }
    }

    /// <summary>
    /// Records the one request-time material selection that ContextBuilder.Build
    /// just decided, so the request dump can prove which material this payload
    /// received. Selection is pure diagnostics: it changes nothing about the
    /// payload. A dedicated file makes it inspectable without an HTTP request.
    /// </summary>
    internal static void PublishMaterialSelection(BridgeConfig config, string selectionJson, string context)
    {
        if (config == null || string.IsNullOrWhiteSpace(selectionJson) || string.IsNullOrEmpty(context))
            return;
        lock (MaterialSelectionLock)
        {
            _materialSelectionJson = selectionJson;
            _materialSelectionContext = context;
            _materialSelectionAt = DateTimeOffset.UtcNow;
        }
        if (!config.PromptDumpEnabled)
            return;
        try
        {
            string root = string.IsNullOrWhiteSpace(config.DataDirectory)
                ? DataPaths.Root()
                : config.DataDirectory;
            string dir = Path.Combine(root, "telemetry", "material_selection");
            Directory.CreateDirectory(dir);
            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
                + "-" + Interlocked.Increment(ref _sequence).ToString("D4")
                + "-" + Guid.NewGuid().ToString("N")[..6];
            string body = "{\"kind\":\"context_material_selection_file\",\"schema_version\":1"
                + ",\"context_chars\":" + context.Length
                + ",\"selection\":" + selectionJson + "}";
            File.WriteAllText(Path.Combine(dir, id + ".json"), body, Encoding.UTF8);
        }
        catch
        {
            // Diagnostic dumping must never break the request path.
        }
    }

    private static JsonElement? LatestMaterialSelection(JsonElement payload)
    {
        string json;
        string context;
        lock (MaterialSelectionLock)
        {
            if (_materialSelectionJson.Length == 0 || _materialSelectionContext.Length < 8)
                return null;
            if (DateTimeOffset.UtcNow - _materialSelectionAt > TimeSpan.FromMinutes(2))
                return null;
            json = _materialSelectionJson;
            context = _materialSelectionContext;
        }
        if (!PayloadCarriesContext(payload, context))
            return null;
        try
        {
            using JsonDocument selection = JsonDocument.Parse(json);
            return selection.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static bool PayloadCarriesContext(JsonElement payload, string context)
    {
        if (!payload.TryGetProperty("messages", out JsonElement messages)
            || messages.ValueKind != JsonValueKind.Array)
            return false;
        foreach (JsonElement message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out JsonElement content))
                continue;
            if (content.ValueKind == JsonValueKind.String
                && TextCarriesContext(content.GetString(), context))
                return true;
            if (content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type)
                    && type.GetString() == "text"
                    && block.TryGetProperty("text", out JsonElement text)
                    && TextCarriesContext(text.GetString(), context))
                    return true;
            }
        }
        return false;
    }

    private static bool TextCarriesContext(string? text, string context)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.Contains(context, StringComparison.Ordinal))
            return true;
        // Source-envelope requests carry the context inside nested material JSON.
        if (!text.Contains("\"runtime_context\"", StringComparison.Ordinal))
            return false;
        try
        {
            using JsonDocument material = JsonDocument.Parse(text);
            return material.RootElement.TryGetProperty("runtime_context", out JsonElement runtime)
                && runtime.ValueKind == JsonValueKind.String
                && runtime.GetString() is { } value
                && value.Contains(context, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static object DescribePayload(JsonElement payload, HistoryProjectionSummary? historyProjection = null)
    {
        var messages = payload.GetProperty("messages").EnumerateArray().Select((message, index) =>
        {
            JsonElement content = message.TryGetProperty("content", out var suppliedContent) ? suppliedContent : default;
            int textChars = 0, imageCount = 0;
            if (content.ValueKind == JsonValueKind.String)
                textChars = (content.GetString() ?? "").Length;
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out JsonElement type)) continue;
                    if (type.GetString() == "text" && block.TryGetProperty("text", out JsonElement text))
                        textChars += (text.GetString() ?? "").Length;
                    else if (type.GetString() == "image_url") imageCount++;
                }
            }
            int reasoningChars = message.TryGetProperty("reasoning_content", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.String ? (reasoning.GetString() ?? "").Length : 0;
            int toolCalls = message.TryGetProperty("tool_calls", out var calls)
                && calls.ValueKind == JsonValueKind.Array ? calls.GetArrayLength() : 0;
            return new { index, role = message.GetProperty("role").GetString(), text_chars = textChars,
                image_count = imageCount, reasoning_chars = reasoningChars, tool_call_count = toolCalls };
        }).ToArray();
        return new { measurement = "characters_not_tokens", message_count = messages.Length,
            text_chars = messages.Sum(m => m.text_chars), image_count = messages.Sum(m => m.image_count),
            reasoning_chars = messages.Sum(m => m.reasoning_chars),
            history_projection = historyProjection == null ? null : new
            {
                mode = historyProjection.Mode,
                available_records = historyProjection.AvailableRecords,
                selected_records = historyProjection.SelectedRecords,
                grouped_replies = historyProjection.GroupedReplies,
                projected_messages = historyProjection.ProjectedMessages,
                projected_entries = historyProjection.ProjectedEntries,
                history_text_chars = historyProjection.TextChars
            },
            messages };
    }
}

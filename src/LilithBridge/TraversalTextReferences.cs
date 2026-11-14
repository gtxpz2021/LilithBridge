using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

// A projection of this actual pending transcript, not a retained reading cache.
// Keep every new page's metadata and order; only an identical inline source
// entry's text can point back to an unambiguous earlier native-tool result.
internal static class TraversalTextReferences
{
    private sealed record InlineText(string CallId, int EntryIndex);
    private static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Page(JsonObject? page) => String(page?["mode"]) == "message"
        && String(page?["message_scope"]) is "exchange" or "before" or "after"
        && String(page?["status"]) is "traversal_page" or "selected_traversal_exhausted"
        && page?["entries"] is JsonArray;
    private static string? Key(JsonObject? entry)
    {
        if (String(entry?["entry_kind"]) != "source" || String(entry?["text"]) is not { Length: > 0 }
            || entry!.ContainsKey("text_ref")) return null;
        // Include the full source identity, range, role, time, display/correction
        // metadata and text, not merely equal words or a locator.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.ToJsonString(DialogueMaterialEnvelope.DataJson))));
    }
    private static bool Smaller(string projected, string original) => Encoding.UTF8.GetByteCount(projected) < Encoding.UTF8.GetByteCount(original)
        && Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(projected)) < Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(original));

    internal static string Project(string json, IReadOnlyList<object> transcript, string currentCallId)
    {
        try
        {
            var page = JsonNode.Parse(json) as JsonObject;
            if (!Page(page) || currentCallId.Length == 0) return json;
            var messages = transcript.Select(m => JsonSerializer.SerializeToElement(m, DialogueMaterialEnvelope.DataJson)).ToArray();
            var declarations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role)) continue;
                if (role.GetString() == "assistant" && message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                            || !call.TryGetProperty("function", out var function) || !function.TryGetProperty("name", out var name)
                            || name.ValueKind != JsonValueKind.String) continue;
                        string key = id.GetString()!;
                        if (!declarations.TryGetValue(key, out var names)) declarations[key] = names = new();
                        names.Add(name.GetString()!);
                    }
                if (role.GetString() == "tool" && message.TryGetProperty("tool_call_id", out var callId) && callId.ValueKind == JsonValueKind.String
                    && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    string key = callId.GetString()!;
                    if (!results.TryGetValue(key, out var bodies)) results[key] = bodies = new();
                    bodies.Add(content.GetString()!);
                }
            }
            // A reused protocol ID makes a text pointer ambiguous. Keep the
            // original page; do not add a new rejection or execution policy.
            if (results.ContainsKey(currentCallId)
                || declarations.TryGetValue(currentCallId, out var current) && current.Count != 1) return json;
            var inline = new Dictionary<string, InlineText>(StringComparer.Ordinal);
            foreach (var pair in results)
            {
                if (pair.Value.Count != 1 || !declarations.TryGetValue(pair.Key, out var names)
                    || names.Count != 1 || names[0] != ReviewContext.ToolName) continue;
                var original = JsonNode.Parse(pair.Value[0]) as JsonObject;
                if (!Page(original)) continue;
                var entries = (JsonArray)original!["entries"]!;
                for (int i = 0; i < entries.Count; i++)
                    if (Key(entries[i] as JsonObject) is { } key) inline.TryAdd(key, new(pair.Key, i));
            }
            int reused = 0;
            var selected = (JsonArray)page!["entries"]!;
            foreach (var node in selected)
            {
                if (node is not JsonObject entry || Key(entry) is not { } key || !inline.TryGetValue(key, out var original)) continue;
                string before = entry.ToJsonString(DialogueMaterialEnvelope.DataJson);
                var text = entry["text"];
                entry.Remove("text");
                entry["text_ref"] = new JsonObject { ["tool_call_id"] = original.CallId, ["entry_index"] = original.EntryIndex, ["field"] = "text" };
                if (Smaller(entry.ToJsonString(DialogueMaterialEnvelope.DataJson), before)) reused++;
                else { entry.Remove("text_ref"); entry["text"] = text; }
            }
            if (reused == 0) return json;
            page["body_reuse"] = new JsonObject { ["referenced_texts"] = reused,
                ["note"] = "text_ref逐字复用本次转录中指定tool_call_id的entries[entry_index].text；其余身份、范围、顺序和关系仍以本页为准。仅复用仍在场正文，不表示模型理解、玩家读过或新增写入依据。" };
            string projected = page.ToJsonString(DialogueMaterialEnvelope.DataJson);
            return Smaller(projected, json) ? projected : json;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        { return json; } // Optional body reuse never blocks a valid navigation result.
    }
}

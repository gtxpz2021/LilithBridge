using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

// This cache only belongs to the actual native transcript. It reuses a complete,
// byte-identical source entry, never a query, locator or claim of sufficiency.
internal sealed partial class NativeReadReceipts
{
    private sealed record WorkEntry(string CallId, int EntryIndex);
    private readonly Dictionary<string, WorkEntry> _workEntries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workCallIds = new(StringComparer.Ordinal);
    private const string WorkPrefix = "[作品资料·局部节选]\n";
    private const string WorkSuffix = "\n[作品资料结束]";

    internal string ProjectWorkMaterial(string dataVersion, string callId, string json)
    {
        if (callId.Length == 0) return json;
        if (!_workCallIds.Add(callId))
        {
            // Do not resend a transcript whose earlier references would become
            // ambiguous. A valid new query with a new ID is never restricted.
            throw new SemanticMaterialException("work_tool_call_id_reused");
        }
        if (dataVersion.Length == 0) return json;
        JsonObject? body;
        JsonObject? material;
        string raw;
        try
        {
            body = JsonNode.Parse(json) as JsonObject;
            if (body?["status"]?.GetValue<string>() != "ok"
                || body["material"] is not JsonValue value || !value.TryGetValue<string>(out raw!)
                || !raw.StartsWith(WorkPrefix, StringComparison.Ordinal)
                || !raw.EndsWith(WorkSuffix, StringComparison.Ordinal)) return json;
            material = JsonNode.Parse(raw.Substring(WorkPrefix.Length,
                raw.Length - WorkPrefix.Length - WorkSuffix.Length)) as JsonObject;
            if (material?["kind"]?.GetValue<string>() != "work_reference_data"
                || material["schema_version"]?.GetValue<int>() != 1
                || material["entries"] is not JsonArray { Count: > 0 and <= 2 }
                || material["archive_sha256"]?.GetValue<string>() != body["archive_sha256"]?.GetValue<string>()) return json;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { return json; }

        var entries = (JsonArray)material["entries"]!;
        var pending = new List<(string Key, int Index)>();
        int reused = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JsonObject entry) return json;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { dataVersion, entry }, DialogueMaterialEnvelope.DataJson))));
            if (_workEntries.TryGetValue(key, out var original) && original.CallId != callId)
            {
                // Reference the first inline entry, not a later reference, so
                // reconstruction never depends on an unbounded reference chain.
                entries[i] = new JsonObject { ["entry_ref"] = new JsonObject {
                    ["tool_name"] = "recall_work_reference", ["tool_call_id"] = original.CallId, ["entry_index"] = original.EntryIndex } };
                reused++;
            }
            else pending.Add((key, i));
        }
        if (reused > 0)
        {
            material["schema_version"] = 2;
            body!["material"] = WorkPrefix + material.ToJsonString(DialogueMaterialEnvelope.DataJson) + WorkSuffix;
            body["material_progress"] = new JsonObject {
                ["inline_entries"] = entries.Count - reused, ["reused_entries"] = reused,
                ["note"] = "entry_ref逐字复用本轮仍在场的recall_work_reference指定tool_call_id中material的entries[entry_index]；新片段仍完整给出。重复命中不表示问题已解决，也不禁止按需要继续查找。" };
            string projected = body.ToJsonString(DialogueMaterialEnvelope.DataJson);
            // The tool result is itself an escaped JSON string in the real
            // payload. Require a reduction in both supported string encodings,
            // not only the unescaped inner material representation.
            if (Encoding.UTF8.GetByteCount(projected) < Encoding.UTF8.GetByteCount(json)
                && Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(projected))
                    < Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(json)))
            {
                foreach (var p in pending) _workEntries.TryAdd(p.Key, new(callId, p.Index));
                return projected;
            }
            // For tiny entries references may cost more than the original.
            // Keep the full original and remember its actual inline locations.
            pending.Clear();
            var originalEntries = (JsonArray)JsonNode.Parse(raw.Substring(WorkPrefix.Length,
                raw.Length - WorkPrefix.Length - WorkSuffix.Length))!["entries"]!;
            for (int i = 0; i < originalEntries.Count; i++)
            {
                var entry = originalEntries[i];
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { dataVersion, entry }, DialogueMaterialEnvelope.DataJson))));
                pending.Add((key, i));
            }
        }
        foreach (var p in pending) _workEntries.TryAdd(p.Key, new(callId, p.Index));
        return json;
    }
}

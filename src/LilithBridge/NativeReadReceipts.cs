using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

// Deduplicate an identical read body only while its original tool result remains
// in this completion's transcript. Different queries, offsets, versions, results
// and tools stay independent; reconsider and other writes never enter this cache.
internal sealed partial class NativeReadReceipts
{
    private readonly Dictionary<string, string> _supplied = new(StringComparer.Ordinal);
    internal int DuplicateBodies { get; private set; }
    internal void Forget(string callId)
    {
        foreach(var key in _supplied.Where(p=>p.Value==callId).Select(p=>p.Key).ToArray())_supplied.Remove(key);
        foreach(var key in _workEntries.Where(p=>p.Value.CallId==callId).Select(p=>p.Key).ToArray())_workEntries.Remove(key);
    }
    private static JsonNode? Copy(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

    internal string Project(string tool, string parameters, string dataVersion, string callId, string json,bool remember=true)
    {
        if (dataVersion.Length == 0) return json;
        var body = JsonNode.Parse(json)?.AsObject();
        if (body == null) return json;
        // Request-budget counters change after every call; the data and result do
        // not. Keep their fresh values in the compact receipt, not its identity.
        var used = Copy(body["used_calls"]);
        var remaining = Copy(body["remaining_calls"]);
        var reconsider = Copy(body["remaining_reconsider"]);
        body.Remove("used_calls"); body.Remove("remaining_calls"); body.Remove("remaining_reconsider");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { tool, parameters, dataVersion, body }, DialogueMaterialEnvelope.DataJson))));
        if (!_supplied.TryGetValue(key, out string? original))
        {
            if(remember)_supplied.Add(key, callId);
            return json;
        }
        if (original == callId) return json; // Never point a repeated protocol ID at itself.
        if(remember)DuplicateBodies++;
        bool blocked = body["can_rephrase"] is JsonValue flag && flag.TryGetValue<bool>(out bool allowed) && !allowed;
        // Only an explicit current read balance can close this advice. Other
        // tools omit that field and may still permit a changed lookup.
        bool exhausted = remaining is JsonValue balance && balance.TryGetValue<int>(out int left) && left == 0;
        var receipt = new JsonObject
        {
            ["status"] = "already_supplied",
            ["original_tool_call_id"] = original,
            ["original_status"] = Copy(body["status"]),
            ["used_calls"] = used, ["remaining_calls"] = remaining, ["remaining_reconsider"] = reconsider,
            ["note"] = exhausted
                ? "同一工具、参数、资料版本和结果已在本轮上面的工具回复中提供；复用该结果。本轮读取余额已用完；仍有未供材料不表示当前可继续读取，也不表示问题已解决。"
                : blocked
                ? "同一工具、参数、资料版本和结果已在本轮上面的工具回复中提供；复用该结果。来源或许可条件未变化，改写查询不能解决。"
                : "同一工具、参数、资料版本和结果已在本轮上面的工具回复中提供；复用该结果。仍可改写查询或继续扩查。"
        };
        if (body.ContainsKey("reason")) receipt["reason"] = Copy(body["reason"]);
        if (body.ContainsKey("can_rephrase")) receipt["can_rephrase"] = Copy(body["can_rephrase"]);
        return receipt.ToJsonString(DialogueMaterialEnvelope.DataJson);
    }
}

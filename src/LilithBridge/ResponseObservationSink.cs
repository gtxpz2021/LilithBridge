using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

// Local diagnostic evidence only. No reader in prompt assembly, memory,
// reflection, speech or action execution. Disabled unless explicitly configured.
internal static class ResponseObservationSink
{
    internal const int MaxFiles = 256;
    internal const int MaxTextChars = 65536;
    private static readonly object WriteLock = new();
    private sealed record TextField(string State, string? Text, int CharsUtf16,
        string Sha256, bool Truncated);

    private static TextField ReadField(JsonElement message, string name)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty(name, out var field))
            return new("missing", null, 0, "", false);
        if (field.ValueKind == JsonValueKind.Null) return new("null", null, 0, "", false);
        if (field.ValueKind != JsonValueKind.String) return new("unsupported_" + field.ValueKind, null, 0, "", false);
        string full = field.GetString() ?? "";
        return BoundText(full.Length == 0 ? "empty_string" : "string", full);
    }

    private static TextField ReadToolCalls(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("tool_calls", out var calls))
            return new("missing", null, 0, "", false);
        if (calls.ValueKind == JsonValueKind.Null) return new("null", null, 0, "", false);
        return BoundText("json_" + calls.ValueKind, calls.GetRawText());
    }

    private static TextField BoundText(string state, string full)
    {
        bool truncated = full.Length > MaxTextChars;
        int length = Math.Min(full.Length, MaxTextChars);
        if (truncated && length > 0 && char.IsHighSurrogate(full[length - 1])) length--;
        return new(state, full[..length], full.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))), truncated);
    }

    private static int CountImages(JsonElement request)
    {
        if (!request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return 0;
        int count = 0;
        foreach (var message in messages.EnumerateArray())
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var type) && type.GetString() == "image_url") count++;
        return count;
    }

    public static void Write(BridgeConfig config, string kind, string purpose,
        string requestJson, string rawResponse, ApiResult result)
    {
        if (!config.ResponseObservationEnabled || kind != "main" || result.DialogueRequest == null) return;
        if (result.DialogueRequest.IsCancelled) { result.Response.ObservationOutcome = "cancelled_before_observation"; return; }
        string? temporary = null;
        try
        {
            JsonElement message = default, choice = default, usage = default;
            bool validJson = true;
            JsonDocument? response = null;
            try { response = JsonDocument.Parse(rawResponse); }
            catch (JsonException) { validJson = false; }
            using (response)
            using (var request = JsonDocument.Parse(requestJson))
            {
                var root = response?.RootElement ?? default;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("usage", out var u)) usage = u;
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        choice = choices[0];
                        if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("message", out var m)) message = m;
                    }
                }
                var generation = request.RootElement;
                var d = result.Response;
                var record = new
                {
                    schema_version = 1, timestamp = DateTimeOffset.Now,
                    request_id = result.RequestId, attempt_id = d.AttemptId,
                    source_user_event_id = result.SourceUserEventId,
                    request_kind = kind, request_purpose = purpose,
                    requested_model = generation.TryGetProperty("model", out var model) ? model.Clone() : (JsonElement?)null,
                    thinking = generation.TryGetProperty("thinking", out var thinking) ? thinking.Clone() : (JsonElement?)null,
                    reasoning_effort = generation.TryGetProperty("reasoning_effort", out var effort) ? effort.Clone() : (JsonElement?)null,
                    max_tokens = generation.TryGetProperty("max_tokens", out var budget) ? budget.Clone() : (JsonElement?)null,
                    image_count = CountImages(generation),
                    native_tools_sent = result.NativeToolsSent,
                    tool_choice = generation.TryGetProperty("tool_choice", out var selection) ? selection.Clone() : (JsonElement?)null,
                    response_id = d.ResponseId, response_model = d.ResponseModel,
                    model_fingerprint = d.ModelFingerprint, http_status = d.HttpStatus,
                    finish_reason = d.FinishReason, validation_outcome = d.Outcome,
                    response_json_valid = validJson,
                    provider_usage = usage.ValueKind != JsonValueKind.Undefined ? usage.Clone() : (JsonElement?)null,
                    message_fields = message.ValueKind == JsonValueKind.Object ? message.EnumerateObject().Select(p => p.Name).ToArray() : Array.Empty<string>(),
                    reasoning_content = ReadField(message, "reasoning_content"),
                    content = ReadField(message, "content"),
                    tool_calls = ReadToolCalls(message),
                    meaning = "Provider-exposed response text; diagnostic only, not displayed speech or a character memory.",
                    max_text_chars_utf16 = MaxTextChars, retention_max_files = MaxFiles
                };
                lock (WriteLock)
                {
                    if (result.DialogueRequest.IsCancelled) { d.ObservationOutcome = "cancelled_before_observation"; return; }
                    string dataRoot = string.IsNullOrWhiteSpace(config.DataDirectory) ? DataPaths.Root() : config.DataDirectory;
                    string dir = Path.Combine(dataRoot, "telemetry", "response_observations");
                    Directory.CreateDirectory(dir);
                    string name = "response-" + d.AttemptId + ".json";
                    string path = Path.Combine(dir, name);
                    temporary = path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                    File.Move(temporary, path); temporary = null;
                    d.ObservationFile = name;
                    d.ObservationOutcome = "saved";
                    try
                    {
                        var files = Directory.GetFiles(dir, "response-*.json")
                            .OrderBy(File.GetLastWriteTimeUtc).ThenBy(p => p, StringComparer.Ordinal).ToArray();
                        foreach (string old in files.Take(Math.Max(0, files.Length - MaxFiles))) File.Delete(old);
                    }
                    catch { d.ObservationOutcome = "saved_retention_failed"; }
                }
            }
        }
        catch
        {
            result.Response.ObservationOutcome = "write_failed";
            if (temporary != null) try { File.Delete(temporary); } catch { }
        }
    }
}

using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    internal const string WorkReferenceToolName = "recall_work_reference";

    private static object WorkReferenceToolDefinition(DialogueRequest request) => new
    {
        type = "function",
        function = new
        {
            name = WorkReferenceToolName,
            description = "查询当前选定的第一作本地资料，可根据对话线索改写关键词、继续扩查。"
                + "返回片段、来源、说话人和分支记录或查询状态；本库记录作品文本，未记录本机玩家的路线选择。"
                + request.WorkReferencePositionOffer(),
            parameters = new { type = "object", properties = new
                { query = new { type = "string", description = "作品情节、人物、场景或相关别名，2至500字符。" } },
                required = new[] { "query" }, additionalProperties = false }
        }
    };

    private static string? ReadWorkReferenceCall(string arguments, string id, JsonElement message,
        IReadOnlyList<RejectedToolCall>? extras, ApiResult envelope)
    {
        var request = envelope.DialogueRequest!;
        if (envelope.NativeToolOffer?.WorkReference != true || !request.StoryToolEnabled)
            return RejectCompletion(envelope, "rejected_work_reference", "Work reference was not declared for this HTTP");
        string query = "", error = "";
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("query", out var value) || value.ValueKind != JsonValueKind.String
                || (query = value.GetString() ?? "").Trim().Length is < 2 or > 500)
                error = "query must be a string of 2 to 500 characters";
        }
        catch (JsonException) { error = "arguments were not valid JSON"; }
        envelope.PendingNativeCall = new(NativeToolKind.WorkReference, id, message.Clone(),
            Error: error, RejectedExtras: extras, WorkQuery: query);
        envelope.Response.Outcome = error.Length == 0 ? "accepted_tool_call" : "accepted_tool_call_invalid";
        return "";
    }

    private static string ExecuteWorkReference(DialogueRequest request, PendingNativeCall call, NativeReadReceipts receipts)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        request.Token.ThrowIfCancellationRequested();
        if (call.Error.Length > 0)
            return TraceWorkReference(request, call, elapsed,
                ReviewContext.Serialize(new { status = "invalid_arguments", reason = call.Error, can_rephrase = true }),
                "invalid_arguments", "invalid_query", true);
        var config = request.RequestConfig;
        if (!PreviousStoryArchive.CurrentSelectionPermitted(config))
            return TraceWorkReference(request, call, elapsed,
                ReviewContext.Serialize(new { status = "unavailable", reason = "work_source_or_consent_changed", can_rephrase = false }),
                "unavailable", "work_source_or_consent_changed", false);
        var recall = PreviousStoryArchive.Recall(call.WorkQuery, config, request.ReviewCurrentText);
        Func<bool>? permission = null;
        if (recall.Found)
        {
            permission = PreviousStoryArchive.CaptureSupplyPermission(config, recall.Content);
            if (permission?.Invoke() != true)
                return TraceWorkReference(request, call, elapsed,
                    ReviewContext.Serialize(new { status = "unavailable", reason = "work_source_or_consent_changed", can_rephrase = false }),
                    "unavailable", "work_source_or_consent_changed", false);
            // Work references are not MemoryStore method-source identities.
            // Preserve ordinary dialogue, but do not retain derived read methods
            // as though their only evidence were the ordinary memory base.
            request.AddStorySupply(permission);
        }
        bool canRephrase = recall.Found || recall.ReasonCode is "no_matching_material" or "invalid_query";
        string note = recall.Found ? "所选第一作资料及来源信息。" : recall.Content;
        string json = ReviewContext.Serialize(new
        {
            status = recall.Found ? "ok" : "not_found", query = call.WorkQuery,
            reason = recall.ReasonCode, can_rephrase = canRephrase,
            archive_sha256 = recall.ArchiveDigest,
            material = recall.Found ? recall.Content : "",
            note
        });
        string version = System.Text.Json.JsonSerializer.Serialize(new
        {
            config.DataDirectory, config.PreviousStoryFile, recall.ArchiveDigest,
            config.PreviousStoryRecallMaxChars
        });
        int duplicatesBefore = receipts.DuplicateBodies;
        string result = receipts.Project(WorkReferenceToolName, call.WorkQuery,
            recall.ArchiveDigest.Length == 0 ? "" : version, call.Id, json);
        result = receipts.ProjectWorkMaterial(version, call.Id, result);
        if (permission != null) request.StageWorkReferencePositions(call.Id, result, permission);
        request.StageB1WorkReadIndex(call.Id, result);
        return TraceWorkReference(request, call, elapsed, result, recall.Found ? "ok" : "not_found",
            recall.ReasonCode, canRephrase, recall.ArchiveDigest, recall.Found ? recall.Content.Length : 0,
            receipts.DuplicateBodies > duplicatesBefore, recall.Found ? recall.Content : "");
    }

    // Content-free locators distinguish genuinely new retrieval from query
    // rewrites over the same source windows. They do not assert relevance.
    private static object[] WorkReferenceSourceLocators(string material)
    {
        try
        {
            using var doc = JsonDocument.Parse(material.Split('\n')[1]);
            return doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => {
                var rows = e.TryGetProperty("lines", out var lines)
                    ? lines.EnumerateArray().Select(l => l.GetProperty("corpus_row").GetInt32()).ToArray()
                    : Array.Empty<int>();
                return (object)new { entry_id = e.GetProperty("id").GetString(), locator = e.GetProperty("locator").GetString(),
                    line_count = rows.Length, first_row = rows.Length == 0 ? (int?)null : rows.Min(),
                    last_row = rows.Length == 0 ? (int?)null : rows.Max() };
            }).ToArray();
        }
        catch { return Array.Empty<object>(); }
    }
    private static int WorkReferenceReusedEntries(string result)
    {
        try { using var doc = JsonDocument.Parse(result); return doc.RootElement.TryGetProperty("material_progress", out var p)
            ? p.GetProperty("reused_entries").GetInt32() : 0; }
        catch { return 0; }
    }

    private static string TraceWorkReference(DialogueRequest request, PendingNativeCall call,
        System.Diagnostics.Stopwatch elapsed, string result, string status, string reason, bool canRephrase,
        string archiveDigest = "", int materialChars = 0, bool duplicateBody = false, string material = "")
    {
        try
        {
            NativeEventLog.Trace("work.reference", "mod", "system", "selected work source lookup receipt",
                ReviewContext.Serialize(new {
                    request_id = request.Id, tool_call_id = call.Id, source_user_event_id = request.UserEventId,
                    tool = WorkReferenceToolName, status, reason, can_rephrase = canRephrase,
                    archive_sha256 = archiveDigest,
                    query_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(call.WorkQuery))),
                    query_chars = call.WorkQuery.Length, material_chars = materialChars,
                    result_chars = result.Length, duplicate_body = duplicateBody,
                    selected_sources = WorkReferenceSourceLocators(material),
                    reused_entries = WorkReferenceReusedEntries(result),
                    elapsed_ms = elapsed.Elapsed.TotalMilliseconds
                }));
        }
        catch { /* Diagnostic failure must never change the returned tool result. */ }
        return result;
    }
}

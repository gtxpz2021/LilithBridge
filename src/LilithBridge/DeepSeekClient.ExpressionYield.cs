using System.Text.Json;

namespace LilithBridge;

internal sealed record ExpressionYieldArgs(string Text, string Supersedes);

internal sealed partial class DeepSeekClient
{
    internal const string ExpressionYieldToolName = "express_and_continue";
    private static object ExpressionYieldToolDefinition() => new
    {
        type = "function",
        function = new
        {
            name = ExpressionYieldToolName,
            description = "可选地先交付一段对玩家有意义的公开表达，然后继续同一问题的取材或思考。"
                + "仅当已有能独立成立的局部回答、且确有后续工作时使用；能直接回答就直接完成。"
                + "不要为显示进度凑空话，不要承诺不存在的后台任务，不要公开私密推理或假称已经查到结果。"
                + "需要工具时优先直接调用该工具，并可在公开content表达，无需额外调用本工具。"
                + "每次交流最多一次，消耗原有HTTP预算；返回排队回执，只有后续显示回执证明程序出现，玩家是否读过未知。"
                + "text是唯一公开台词；后续接着补充，避免完整重复。supersedes仅在明确修正已有segment时填写其原ID。",
            parameters = new
            {
                type = "object", properties = new
                {
                    text = new { type = "string", minLength = 1, maxLength = 4096, description = "准备立即对玩家表达的台词，不放思考过程或控制指令。" },
                    supersedes = new { type = "string", description = "可选，被明确修正的此前segment ID；不修正时省略。" }
                },
                required = new[] { "text" }, additionalProperties = false
            }
        }
    };

    private static string? ReadExpressionYieldCall(string arguments, string id, JsonElement message,
        IReadOnlyList<RejectedToolCall>? extras, ApiResult envelope)
    {
        var owner = envelope.DialogueRequest!;
        if (envelope.NativeToolOffer?.ExpressionYield != true || owner.WorkPrototype?.CanYield != true)
            return RejectCompletion(envelope, "rejected_expression_yield", "Expression yield was not declared for this HTTP or already used");
        string error = ""; ExpressionYieldArgs? expression = null;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            string text = "", supersedes = "";
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("text" or "supersedes"))
                || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                || !root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text = t.GetString() ?? "") || text.Length > 4096
                || root.TryGetProperty("supersedes", out var s) && (s.ValueKind != JsonValueKind.String || (supersedes = s.GetString() ?? "").Length > 200))
                error = "expected public text and an optional bounded supersedes ID only";
            else if (message.GetProperty("content").ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetProperty("content").GetString()))
                error = "put the single public expression in text; separate content would create ambiguous expression identity";
            else if (!TryParsePublicExpression(text, owner.ReviewCurrentText, out var parsed))
                error = "text must contain expression only";
            else expression = new(parsed!.Text, supersedes);
        }
        catch (JsonException) { error = "arguments were not valid JSON"; }
        if (!owner.CanExecuteNativeTool)
            return RejectCompletion(envelope, "attempt_limit", "No continuation budget remains");
        envelope.PendingNativeCall = new(NativeToolKind.ExpressionYield, id, message.Clone(), Error: error,
            RejectedExtras: extras, Expression: expression);
        envelope.Response.Outcome = error.Length == 0 ? "accepted_tool_call" : "accepted_tool_call_invalid";
        return "";
    }

    private static string ExecuteExpressionYield(DialogueRequest owner, PendingNativeCall call, ApiResult response,MemoryStore? store=null)
    {
        owner.Token.ThrowIfCancellationRequested();
        if (call.Error.Length > 0 || call.Expression == null)
            return ReviewContext.Serialize(new { status = "not_expressed", reason = "invalid_arguments", error = call.Error });
        if (!WorkSourcesPermitted(response, owner) || owner.WorkPrototype is not { } work
            || work.OperationId != owner.Id || !owner.CanExecuteNativeTool)
            return ReviewContext.Serialize(new { status = "not_expressed", reason = "owner_or_source_changed" });
        work.SourceUserEventId = owner.UserEventId;
        bool accepted;string segment;
        work.HoldArchivePublication(response.Response.AttemptId);
        try
        {
            accepted = work.OfferYield(call.Expression.Text, response.Response.AttemptId,
                CaptureWorkSourcesPermission(response, owner), call.Expression.Supersedes, out segment);
            if(accepted)ArchiveOfferedExpression(store,owner,response,segment);
        }
        finally { work.PublishAfterArchive(response.Response.AttemptId); }
        return ReviewContext.Serialize(new { status = accepted ? "expression_queued" : "not_expressed",
            segment_id = segment, attempt_id = response.Response.AttemptId,
            display = "not_confirmed;consult_temporary_expression_receipt", player_read = "unknown",
            continuation = "same_owner_and_original_budget;no_new_background_owner" });
    }
}

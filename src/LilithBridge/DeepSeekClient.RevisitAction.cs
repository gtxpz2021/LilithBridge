using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    internal const string RevisitActionToolName = "revisit_thought";
    private static object RevisitActionToolDefinition(ThoughtRevisitTarget[] targets) => new
    {
        type = "function", function = new
        {
            name = RevisitActionToolName,
            description = "可选：从本次已看到的思想中指定一条，请求一次后台重访。reason 简短说明想回看什么，不写私密推理。"
                + "返回受理、忙碌或版本变化后继续交流；受理不等于完成，不必等待或插话。每次交流只尝试一次，不自动续想。"
                + "可选对象如下（最多列32条；text 是已供正文的定位摘句，不是重要性排序；版本须照填）："
                + ReviewContext.Serialize(targets.Select(t => new { history_ref = "note:" + t.Target.EventId,
                    version = t.Target.Fingerprint, location = t.Location, text = RevisitLocatorText(t.VisibleText) })),
            parameters = new
            {
                type = "object", properties = new
                {
                    history_ref = new { type = "string", @enum = targets.Select(t => "note:" + t.Target.EventId).Distinct().ToArray() },
                    version = new { type = "string", minLength = 64, maxLength = 64 },
                    reason = new { type = "string", minLength = 1, maxLength = 600 }
                }, required = new[] { "history_ref", "version", "reason" }, additionalProperties = false
            }
        }
    };

    private static string RevisitLocatorText(string text)
    {
        int length = Math.Min(text.Length, 96);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    internal static ThoughtRevisitChoice? ParseRevisitAction(string json, ThoughtRevisitTarget[] offered, out string error)
    {
        error = "invalid_arguments";
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || root.EnumerateObject().Any(p => p.Name is not ("history_ref" or "version" or "reason") || p.Value.ValueKind != JsonValueKind.String)
                || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) return null;
            string reference = root.GetProperty("history_ref").GetString()!, version = root.GetProperty("version").GetString()!, reason = root.GetProperty("reason").GetString()!;
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 600) return null;
            error = "target_not_offered_for_this_http";
            var matches = offered.Where(t => "note:" + t.Target.EventId == reference && t.Target.Fingerprint == version).ToArray();
            if (matches.Length != 1) return null;
            error = ""; return new(matches[0].Target, reason);
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadRevisitActionCall(string arguments, string id, JsonElement message,
        IReadOnlyList<RejectedToolCall>? extras, ApiResult envelope)
    {
        var request = envelope.DialogueRequest!;
        if (envelope.NativeToolOffer?.RevisitTargets is not { Length: > 0 } offered)
            return RejectCompletion(envelope, "rejected_revisit_action", "Thought revisit was not declared for this HTTP");
        var choice = ParseRevisitAction(arguments, offered, out string error);
        if (!request.CanExecuteNativeTool) return RejectCompletion(envelope, "attempt_limit", "No continuation budget remains");
        envelope.PendingNativeCall = new(NativeToolKind.RevisitAction, id, message.Clone(), Error: error,
            RejectedExtras: extras, Revisit: choice);
        envelope.Response.Outcome = error.Length == 0 ? "accepted_tool_call" : "accepted_tool_call_invalid";
        return "";
    }

    private static async Task<string> ExecuteRevisitAction(DialogueRequest request, PendingNativeCall call, ApiResult response)
    {
        ThoughtRevisitReceipt receipt;
        if (call.Revisit == null || call.Error.Length > 0) receipt = new("not_accepted", call.Error);
        else if (request.RevisitAction == null || !request.CanExecuteNativeTool || !WorkSourcesPermitted(response, request))
            receipt = new("not_accepted", "owner_or_source_changed");
        else
        {
            var permission = CaptureWorkSourcesPermission(response, request);
            // Freeze the reason's actual source footprint before any subsequent HTTP.
            var basis = request.RevisitActionBasis();
            receipt = await request.RevisitAttempt.Run(call.Revisit,
                () => request.RevisitAction(call.Revisit, call.Id, basis, permission)).ConfigureAwait(false);
        }
        return ReviewContext.Serialize(new { status = receipt.Status, reason = receipt.Reason,
            revisit_id = receipt.OperationId, completed = false,
            continuation = "继续当前交流；没有自动续想、等待完成或插话" });
    }
}

using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    // Explicit harness-only opt in. The game controller never calls this entry.
    internal async Task<ApiResult> SendWorkPrototypeAsync(BridgeConfig config, string userText, string context,
        DialogueRequest owner, MemoryStore? memory = null, IReadOnlyList<MemoryTurn>? history = null)
    {
        var work = owner.WorkPrototype ?? throw new InvalidOperationException("Attach a work session explicitly");
        if (work.OperationId != owner.Id) throw new InvalidOperationException("Work owner mismatch");
        ApiResult result = await SendAsync(config, userText, context, null, owner.Token, history,
            dialogueRequest: owner, historyOwner: memory, requestSourcePermission: work.ScopePermitted).ConfigureAwait(false);
        if (result.Error.Length > 0) work.Finish(owner.IsCancelled ? "cancelled" : "failed");
        else
        {
            if (result.Text.Length > 0)
            {
                if (!NativeMemoResultInput.ExpressionOnly(result)
                    || !work.Offer(result.Text, result.Response.AttemptId, true,
                        () => WorkSourcesPermitted(result, owner)))
                { work.Finish("final_expression_withheld"); return result; }
            }
            work.Finish();
        }
        return result;
    }
    private static bool WorkSourcesPermitted(ApiResult response, DialogueRequest request) =>
        response.SendPermission?.Allowed != false && response.RequestSourcesPermitted(includeChatHistory: request.HasChatHistorySupply)
        && request.ReviewSupplyPermitted() && MethodProtocolPermitted(request);

    private static Func<bool> CaptureWorkSourcesPermission(ApiResult response, DialogueRequest request)
    {
        // Freeze the history footprint that could have informed THIS expression.
        // The empty owner execution gate must not erase a received expression on
        // cancellation. Real history leases/withdrawals still revalidate and may
        // conservatively withhold it; no new read grant is created here.
        var history = request.ChatHistorySnapshots.ToArray();
        bool hadReview = request.HasReviewSupply;
        return () => response.SendPermission?.Allowed != false && response.RequestSourcesPermitted(includeChatHistory: false)
            && ReviewSourceSnapshot.TryUseMany(history, () => true)
            && (!hadReview || request.ReviewSupplyPermitted()) && MethodProtocolPermitted(request);
    }

    private static void OfferWorkPrelude(DialogueRequest owner, PendingNativeCall call, ApiResult response, string question,MemoryStore? store=null)
    {
        var work = owner.WorkPrototype;
        if (work == null || call.Kind is NativeToolKind.RejectAll or NativeToolKind.ExpressionYield || call.Error.Length > 0
            || !WorkSourcesPermitted(response, owner)
            || !call.AssistantMessage.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String) return;
        string raw = content.GetString() ?? "";
        if (TryParsePublicExpression(raw, question, out var expression))
        {
            work.SourceUserEventId = owner.UserEventId;
            work.HoldArchivePublication(response.Response.AttemptId);
            try
            {
                if(work.Offer(expression!.Text, response.Response.AttemptId, false, CaptureWorkSourcesPermission(response, owner)))
                {
                    var segment=work.ArchiveSnapshot().Single(s=>s.AttemptId==response.Response.AttemptId&&!s.Final&&s.StreamItem==null);
                    ArchiveOfferedExpression(store,owner,response,segment.Id);
                }
            }
            finally { work.PublishAfterArchive(response.Response.AttemptId); }
        }
    }

    private static bool TryParsePublicExpression(string raw, string question, out ApiResult? expression)
    {
        expression = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // Reject malformed/unknown JSON before ParseReply's intentional plain
        // text fallback. This prototype accepts only expression-only JSON.
        string clean = StripJsonFences(raw).Trim();
        if (clean.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(clean);
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || json.RootElement.EnumerateObject().Count() != 1
                    || !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return false;
            }
            catch (JsonException) { return false; }
        }
        expression = ParseReply(raw, question);
        return NativeMemoResultInput.ExpressionOnly(expression) && !string.IsNullOrWhiteSpace(expression.Text);
    }
}

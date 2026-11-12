namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    // This is a program receipt, not model text. The controller must route the
    // history_request_budget outcome through its existing host-only failure
    // notice surface; a successful character bubble would enter AddHistory and
    // delivery-time extraction. Never invent accepted/model reply identity here.
    private static ApiResult CompleteHistoryBudgetLocally(ApiResult envelope, DialogueRequest? request)
    {
        if (envelope.Response.Outcome != "history_request_budget") return envelope;
        if (request != null && envelope.DialogueRequest != null
            && !object.ReferenceEquals(request, envelope.DialogueRequest)) return envelope;
        if (request?.IsCancelled == true || envelope.DialogueRequest?.IsCancelled == true) return envelope;

        var receipt = new ApiResult
        {
            IsLocalFallback = true,
            Error = "回看空间不足，本次未生成答复；输入保留，可继续或缩小范围。",
            Text = "",
            ExplicitEmptyText = false,
            // Keep the true number of earlier completed provider requests. No
            // final completion, token use, retry or HTTP attempt is invented.
            SuccessfulModelRequests = envelope.SuccessfulModelRequests
        };
        receipt.CopyRequestFrom(envelope);
        receipt.CopyResponseFrom(envelope);
        receipt.RawText = "";
        receipt.ReasoningContent = "";

        // CopyRequestFrom retains the original request object and all existing
        // permission/cancellation guards. The metadata-only locating DTO stays
        // solely on that request; never interpolate its source identifiers
        // or private bodies into this host notice or make new B1 aliases.
        // All action, memory, reflection, recall and B1 choice fields on this
        // new ApiResult remain empty defaults. Outcome remains the true budget
        // failure, so native-event linkage continues to settle without speech.
        return receipt;
    }
}

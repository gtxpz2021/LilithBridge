namespace LilithBridge;
internal static class TransportFailurePresentation
{
    // Failed recovery still belongs to the original confirmed transport failure.
    public static bool Handles(ApiResult result) => result.Error.Length>0
        && (result.Response.Outcome=="transport_error"
            || (result.PreviousTransportAttempts.Count>0 && result.Response.Outcome!="cancelled"));
    // Reuse the same host-only notice surface for a structurally empty public reply
    // and a final history budget receipt. Neither becomes a character fallback.
    public static bool HandlesReplyFailure(ApiResult result) => Handles(result)
        || (result.Error.Length > 0 && (result.Response.Outcome is "history_request_budget" or "empty_visible_reply" or "timeout" or "http_error" or "authorization_revoked" or "recall_source_changed"
            || result.Response.Provider=="openai" || result.Response.Outcome.StartsWith("openai_",System.StringComparison.Ordinal)));
    public static string NoticeFor(ApiResult result) => result.Response.Outcome == "history_request_budget"
        ? result.Error : result.Response.Outcome == "empty_visible_reply"
        ? "回复未交付。草稿已保留。" : result.Response.Outcome=="timeout"
        ? "等待已达上限，未收到完整回复。原输入已保留，可重新发送或降低思考强度。"
        : result.Response.Outcome.StartsWith("openai_",System.StringComparison.Ordinal) || result.Response.Outcome is "http_error" or "authorization_revoked" or "recall_source_changed"
        ? result.Error+"；原输入已保留。" : Notice;
    public static string Draft(ApiResult result,string submittedText) => result.SourceUserText.Length>0?result.SourceUserText:submittedText;
    public const string Notice="连接中断，未收到模型回复。草稿已保留。";
}

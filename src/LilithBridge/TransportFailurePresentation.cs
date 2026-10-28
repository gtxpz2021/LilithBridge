namespace LilithBridge;
internal static class TransportFailurePresentation
{
    // Failed recovery still belongs to the original confirmed transport failure.
    public static bool Handles(ApiResult result) => result.Error.Length>0
        && (result.Response.Outcome=="transport_error"
            || (result.PreviousTransportAttempts.Count>0 && result.Response.Outcome!="cancelled"));
    // Reuse the same host-only notice surface for a structurally empty public reply.
    // Keep the transport classifier and its wording distinct from an empty reply.
    public static bool HandlesReplyFailure(ApiResult result) => Handles(result)
        || (result.Error.Length > 0 && result.Response.Outcome == "empty_visible_reply");
    public static string NoticeFor(ApiResult result) => result.Response.Outcome == "empty_visible_reply"
        ? "回复未交付。草稿已保留。" : Notice;
    public static string Draft(ApiResult result,string submittedText) => result.SourceUserText.Length>0?result.SourceUserText:submittedText;
    public const string Notice="连接中断，未收到模型回复。草稿已保留。";
}

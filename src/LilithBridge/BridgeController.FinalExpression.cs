namespace LilithBridge;

internal sealed partial class BridgeController
{
    // Text-only policy shared by ordinary generation and result expression.
    // The caller retains request/response identity and applies operation facts
    // afterwards. Never re-enter ApplyAiResult from a result continuation.
    private ApiResult ApplyReplySafetyFallback(ApiResult result)
    {
        if (!_config.SafetyFallbackEnabled || !PrivacySafety.IsCrisis(result?.Text))
            return result!;
        NativeEventLog.Trace("safety.local_guard", "mod", "system",
            "crisis phrasing in reply replaced by a local supportive line");
        return new ApiResult
        {
            IsLocalFallback = true,
            Text = PrivacySafety.LocalSupportReply(),
            Emotion = "emoji_calm_1",
            LatencyMs = result?.LatencyMs ?? 0
        };
    }
}

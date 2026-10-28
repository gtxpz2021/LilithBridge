using Il2CppInterop.Runtime.Attributes;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private void SubmitProactive(string reason)
    {
        try
        {
            string category = _sessionState.ForegroundApp?.Category ?? "unknown";
            AppAwareRemarkGate.MarkRemarked(SystemSensor.LocalTime, category);
        }
        catch
        {
            // Native cooldown tracking is optional.
        }
        _proactiveRequest = true;
        _currentUserText = ParticipationPrompt.UserText;
        // A proposed topic is not an event she has already participated in.
        // Actual speech records its own AssistantReply; quiet consumes the
        // session-local candidate only after a successful explicit decision.
        NativeEventLog.Trace("meta.vision.deferred", "mod", "system",
            "trial permits model-requested capture only in player dialogue");
        SubmitRequest(
            _currentUserText,
            extraContext: ParticipationPrompt.BuildContext(reason), userEventId: "");
    }

    // Runs before the empty-result fallback and all action / note / memory effects.
    // An accepted explicit empty text is a choice only on the proactive path.
    [HideFromIl2Cpp]
    internal bool TrySettleProactiveSilence(ApiResult result)
    {
        if (!_proactiveRequest || result.DialogueRequest != null
            || !result.ExplicitEmptyText || !string.IsNullOrWhiteSpace(result.Text)
            || result.Error.Length != 0 || result.Response.Outcome != "accepted")
            return false;

        FinishNativeLinkage(result, "quiet");
        _requestPending = false;
        _proactiveRequest = false;
        _currentUserText = "";
        _sessionState.PendingIntent = "";
        _sessionState.MarkConversationStarted();
        result.DeliveryOutcome = "quiet";
        NativeEventLog.Trace("meta.proactive.quiet", "mod", "system",
            "proactive candidate considered; no speech or side effects",
            "request_id=" + result.RequestId + ";attempt_id=" + result.Response.AttemptId);
        return true;
    }
}

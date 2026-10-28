using System;

namespace LilithBridge;

internal sealed record ActionDecision(bool Allow, bool Defer, string Reason)
{
    public static ActionDecision AllowNow() => new(true, false, "now");
    public static ActionDecision DeferNow() => new(true, true, "native_busy");
    public static ActionDecision Deny(string reason) => new(false, false, reason);
}

internal static class ActionDirector
{
    public static void ApplyFallback(
        ApiResult reply,
        string userText,
        string context,
        BridgeConfig config,
        SessionState? state = null)
    {
        string beforeAction = reply?.Action ?? "";
        if (config.AutoActionEnabled
            && reply != null
            && string.IsNullOrWhiteSpace(reply.Action))
        {
            NativeActionSuggester.Apply(reply, userText, context);
        }
        if (reply != null)
            ActionCapability.StripUnavailable(reply, state);
        ActionPathDiagnostic.TraceFallback(beforeAction, reply);
    }

    public static ActionDecision Decide(ApiResult reply, SessionState state, BridgeConfig? config = null)
    {
        if (reply == null)
            return ActionDecision.Deny("empty_reply");

        if (config != null
            && PersonaStateEngine.RepairsAction(reply, state, config, out string actionReason))
        {
            return ActionDecision.AllowNow();
        }

        if (NativeBusy(state))
            return ActionDecision.DeferNow();

        if (!string.IsNullOrWhiteSpace(reply.Action)
            && !ActionCatalog.TryParseAllowedAction(reply.Action, out _))
        {
            NativeEventLog.Trace("action.unavailable_stripped", "mod", "lilith",
                reply.Action, "not in exposed/playable whitelist; reply text preserved");
            reply.Action = "";
            reply.Animation = "";
        }

        if (!string.IsNullOrWhiteSpace(reply.Animation)
            && string.IsNullOrWhiteSpace(reply.Action))
        {
            // An animation alone is not a validated native action. Keep the
            // reply visible and drop the unimplemented animation request.
            NativeEventLog.Trace("action.animation_unavailable", "mod", "lilith",
                reply.Animation, "animation without a valid native action; reply text preserved");
            reply.Animation = "";
        }

        ActionCapability.StripUnavailable(reply, state);
        if (config != null)
        {
            if (!config.ActionSequencesEnabled)
                reply.Actions.Clear();
            else if (reply.Actions.Count > config.ActionSequenceMaxSteps)
                reply.Actions.RemoveRange(
                    config.ActionSequenceMaxSteps,
                    reply.Actions.Count - config.ActionSequenceMaxSteps);
        }

        return ActionDecision.AllowNow();
    }

    public static bool NativeBusy(SessionState state)
    {
        if (state == null)
            return true;
        return state.NativeDialogueActive
            || state.PlayerOptionsShowing
            || state.BusyOrAwaitingResponse
            || state.Native.IsDragging
            || state.Native.IsInteracting;
    }
}

using System;

namespace LilithBridge;

/// <summary>
/// Small coordination boundary for runtime concerns that do not need to know
/// Unity internals: local fallback replies, quiet consolidation scheduling and
/// runtime metadata. This keeps BridgeController focused on native wiring.
/// </summary>
internal static class RuntimeDirector
{
    public static ApiResult BuildLocalReply(
        string? userText,
        ApiResult? original,
        BridgeConfig config)
    {
        bool crisis = PrivacySafety.IsCrisis(userText);
        var fallback = new ApiResult
        {
            IsLocalFallback = true,
            Text = crisis
                ? PrivacySafety.LocalSupportReply()
                : PrivacySafety.LocalFallbackReply(userText),
            Emotion = crisis ? "emoji_calm_1" : "emoji_calm_1",
            Action = crisis ? "" : "confuse",
            LatencyMs = original?.LatencyMs ?? 0
        };
        if (original != null) fallback.CopyResponseFrom(original);
        return fallback;
    }

    public static bool ShouldConsolidateDream(
        BridgeConfig config,
        bool systemReady,
        bool requestPending,
        bool queuedReply,
        bool listening,
        bool aiSpeech,
        bool memoryRecall,
        bool storyRecall,
        double idleSeconds)
    {
        return config.MemoryDreamsEnabled
            && systemReady
            && !requestPending
            && !queuedReply
            && !listening
            && !aiSpeech
            && !memoryRecall
            && !storyRecall
            && idleSeconds >= config.MemoryDreamMinIdleSeconds;
    }

    /// <summary>
    /// Reflection is driven by material, not by a quota, and it is timed to a
    /// passage rather than to a single line: novelty alone no longer starts a pass
    /// (on 2026-09-19 that produced a thought every minute). She thinks once enough
    /// new conversation has accumulated AND the conversation has paused — unless
    /// something actually changed in her memory, or she asked to think herself.
    /// The passage she is shown is the last stretch of conversation, in order.
    /// </summary>
    public static bool ShouldReflect(
        BridgeConfig config,
        bool systemReady,
        bool requestPending,
        bool queuedReply,
        bool listening,
        bool annotationInProgress,
        bool noteSupplementInProgress,
        bool reflectionInProgress,
        double idleSeconds,
        int newDeliveredTurns,
        bool significantChange,
        bool novelMaterial,
        bool requestedByModel,
        bool resumedPendingWindow = false,
        bool settledIncompleteWindow = false,
        int budgetLimitedDialogueLines = 0,
        bool currentDialogue = false,
        bool? currentWindowQualified = null)
    {
        if (!config.ReflectionEnabled
            || !systemReady
            || requestPending
            || queuedReply
            || listening
            || annotationInProgress
            || noteSupplementInProgress
            || reflectionInProgress)
            return false;
        // An explicit public request can use one settled dialogue immediately.
        // Frozen-work/failure cooldown and provider/resource/source gates still
        // apply; this does not create a request on ordinary turns. Timing is not target selection.
        if (requestedByModel && newDeliveredTurns >= 2) return true;
        // Known current-window age plus service spacing replaces only the pure
        // silence pacing. The caller has checked the spacing; all execution,
        // native-state, owner and source guards still use the existing path.
        // Like explicit reflect, this grants an opportunity, not a conclusion.
        if (currentDialogue && currentWindowQualified.HasValue)
            return currentWindowQualified.Value;
        if (newDeliveredTurns < Math.Max(1, config.ReflectionMinNewTurns)
            // A short current conversation should not wait forever for a third
            // exchange. It still waits for the same natural pause; this does not
            // create a reflection per utterance or replace her explicit request.
            && !(currentDialogue && config.ReflectionOnlyWhenIdle && newDeliveredTurns >= 2
                && idleSeconds >= config.ReflectionNaturalPauseSeconds)
            && !(resumedPendingWindow && newDeliveredTurns >= 2)
            // A character-limited prefix may be small even though this same
            // eligible window already met the ordinary complete-dialogue floor.
            && !(newDeliveredTurns >= 2
                && budgetLimitedDialogueLines >= Math.Max(1, config.ReflectionMinNewTurns))
            && !settledIncompleteWindow)
            return false;
        // A real change in what she knows, or her own request, may start a pass at
        // any time. Ordinary material waits for a pause in the conversation, so the
        // passage is over before she looks back at it. `novelMaterial` still feeds
        // the telemetry reason, but it no longer starts a pass on its own.
        _ = novelMaterial;
        return ReflectionIdleOpportunity(config, idleSeconds, significantChange || requestedByModel);
    }

    // The original idle conjunction, shared with fair replacement. This checks
    // no dialogue floor, source, owner, cooldown or progressive-service deadline.
    internal static bool ReflectionIdleOpportunity(BridgeConfig config, double idleSeconds, bool significantChange)
    {
        if (idleSeconds < config.ReflectionMinIdleSeconds) return false;
        return significantChange || idleSeconds >= config.ReflectionNaturalPauseSeconds;
    }

    // Only fresh ordinary old-material admission uses the additional activity
    // fact. Current-session admission and explicit requests keep their policy.
    // Recovery is handled before this seam by the controller.
    internal static double ReflectionSelectionIdleSeconds(BridgeConfig config, bool currentDialogue,
        bool requestedByModel, double nativeIdleSeconds, double? dialogueIdleSeconds) =>
        config.ReflectionOnlyWhenIdle && !currentDialogue && !requestedByModel && dialogueIdleSeconds.HasValue
            ? Math.Min(nativeIdleSeconds, dialogueIdleSeconds.Value) : nativeIdleSeconds;

    /// <summary>Human-readable reason for the telemetry record.</summary>
    /// <summary>
    /// Chooses the retrieval query for one request. Ordinary turns search with
    /// the player's own words; a proactive turn may only continue the topic while
    /// the conversation is still warm, or when its own reason names an unfinished
    /// topic. After a longer gap the finished topic must not be inherited.
    /// </summary>
    public static (string Query, string Source) ChooseRecallQuery(
        bool proactive,
        string candidateQuery,
        string lastUserText,
        int warmContinuationSeconds,
        double idleSeconds)
    {
        if (!proactive)
            return (lastUserText, "player_input");
        if (!string.IsNullOrWhiteSpace(candidateQuery))
            return (candidateQuery, "unfinished_topic");
        if (warmContinuationSeconds > 0 && idleSeconds <= warmContinuationSeconds)
            return (lastUserText, "warm_conversation");
        return ("", "suppressed_stale_topic");
    }

    public static string DescribeReflectionTrigger(
        BridgeConfig config,
        double idleSeconds,
        bool significantChange,
        bool novelMaterial,
        bool requestedByModel)
    {
        if (requestedByModel)
            return "model_request";
        if (significantChange)
            return "meaningful_change";
        // Novelty decides what the material is worth, not when the pass starts, so
        // it is no longer reported as the trigger. What actually opened the pass is
        // the pause in the conversation (or one of the two reasons above).
        _ = novelMaterial;
        return idleSeconds >= config.ReflectionNaturalPauseSeconds
            ? "natural_pause"
            : "none";
    }

    public static string DescribeRuntime(BridgeConfig config)
    {
        return "action_capability=" + config.ActionCapabilityEnabled
            + ";action_sequences=" + config.ActionSequencesEnabled
            + ";memory_honest_recall=" + config.MemoryHonestRecallEnabled
            + ";memory_compression=" + config.MemoryCompressionEnabled
            + ";memory_dreams=" + config.MemoryDreamsEnabled
            + ";reflection_recall_in_chat=" + config.ReflectionRecallInChat
            + ";reflection_min_new_turns=" + config.ReflectionMinNewTurns
            + ";reflection_natural_pause_seconds=" + config.ReflectionNaturalPauseSeconds
            + ";persona_runtime=" + config.PersonaRuntimeEnabled
            + ";world_belief=" + config.WorldBeliefEnabled
            + ";runtime_protocol=" + config.RuntimeProtocolEnabled
            + ";touch_event_repair=" + config.TouchEventRepairEnabled
            + ";privacy_redaction=" + config.PrivacyRedactionEnabled
            + ";api_fallback=" + config.ApiFallbackEnabled
            + ";safety_fallback=" + config.SafetyFallbackEnabled;
    }
}

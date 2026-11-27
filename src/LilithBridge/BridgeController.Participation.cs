using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;

namespace LilithBridge;

/// <summary>
/// Transport-level pacing for proactive participation opportunities (never a
/// programmatic decision substitute or permanent session mute):
/// - Quiet (no bubble, no note) applies a light global recheck (default 90s) + per-kind cooldown;
/// - Quiet Note (silent paper) applies normal base cooldown (default 600s) without unanswered penalty;
/// - Spoken proactive bubbles space transport intervals with bounded backoff (capped by
///   ProactiveMaxUnansweredBeforeQuietMode exponent and 7200s max), while still letting
///   Lilith herself decide on future opportunities;
/// - Any player message immediately resets unanswered count and restores normal base spacing;
/// - late_night latches once per night bucket so the system never re-sends the same night cue.
/// </summary>
internal sealed class ProactivePacingState
{
    private readonly Dictionary<string, float> _kindCooldownUntil = new(StringComparer.Ordinal);
    private float _nextEligibleAt = float.NegativeInfinity;
    private float _lastDispatchedAt = float.NegativeInfinity;
    private int _consecutiveUnansweredSpeeches;
    private string _consumedLateNightBucket = "";

    internal float NextEligibleAt => _nextEligibleAt;
    internal int ConsecutiveUnansweredSpeeches => _consecutiveUnansweredSpeeches;
    internal string ConsumedLateNightBucket => _consumedLateNightBucket;

    internal static string ComputeLateNightBucket(DateTime localNow) =>
        (localNow.Hour < 5 ? localNow.Date.AddDays(-1) : localNow.Date).ToString("yyyy-MM-dd");

    internal bool IsGlobalReady(float now) => now >= _nextEligibleAt;

    internal float RemainingSeconds(float now) => Math.Max(0f, _nextEligibleAt - now);

    internal HashSet<string> BuildSuppressedKinds(float now, DateTime localNow)
    {
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in _kindCooldownUntil)
        {
            if (now < kv.Value)
                suppressed.Add(kv.Key);
        }
        if (_consumedLateNightBucket == ComputeLateNightBucket(localNow))
            suppressed.Add("late_night");
        return suppressed;
    }

    internal void MarkDispatched(float now, string kind, DateTime localNow, BridgeConfig config)
    {
        _lastDispatchedAt = now;
        _nextEligibleAt = now + Math.Max(30, config.ProactiveCooldownSeconds);
        if (string.Equals(kind, "late_night", StringComparison.Ordinal))
            _consumedLateNightBucket = ComputeLateNightBucket(localNow);
    }

    internal void ObserveQuiet(float now, string kind, BridgeConfig config)
    {
        _nextEligibleAt = now + Math.Max(15, config.ProactiveQuietRecheckSeconds);
        if (!string.IsNullOrEmpty(kind))
            _kindCooldownUntil[kind] = now + Math.Max(30, config.ProactiveCooldownSeconds);
    }

    internal void ObserveQuietNote(float now, string kind, BridgeConfig config)
    {
        float cd = Math.Max(30, config.ProactiveCooldownSeconds);
        _nextEligibleAt = now + cd;
        if (!string.IsNullOrEmpty(kind))
            _kindCooldownUntil[kind] = now + cd;
    }

    internal float ObserveSpokenDelivered(float now, string kind, BridgeConfig config)
    {
        _consecutiveUnansweredSpeeches++;
        int cappedExponent = Math.Min(_consecutiveUnansweredSpeeches, Math.Max(1, config.ProactiveMaxUnansweredBeforeQuietMode));
        double mult = Math.Pow(Math.Max(1.0, config.ProactiveUnansweredBackoffMultiplier), cappedExponent);
        float backoffSeconds = (float)Math.Min(7200.0, Math.Max(30, config.ProactiveCooldownSeconds) * mult);
        _nextEligibleAt = now + backoffSeconds;
        if (!string.IsNullOrEmpty(kind))
            _kindCooldownUntil[kind] = now + backoffSeconds;
        return backoffSeconds;
    }

    internal void ObservePlayerReply(float now, BridgeConfig config)
    {
        bool hadPenalty = _consecutiveUnansweredSpeeches > 0;
        _consecutiveUnansweredSpeeches = 0;
        if (hadPenalty && _lastDispatchedAt > float.NegativeInfinity)
            _nextEligibleAt = Math.Min(_nextEligibleAt, _lastDispatchedAt + Math.Max(30, config.ProactiveCooldownSeconds));
    }
}

internal sealed partial class BridgeController
{
    private readonly ProactivePacingState _proactivePacing = new();
    private string _pendingProactiveKind = "";
    private string _activeProactiveKind = "";

    [HideFromIl2Cpp]
    internal ProactivePacingState ProactivePacing => _proactivePacing;

    private void AcknowledgeProactivePlayerReply(float now)
    {
        _proactivePacing.ObservePlayerReply(now, _config);
    }

    private void SubmitProactive(string reason, string kind = "")
    {
        try
        {
            if (reason.StartsWith("玩家现在正在使用：", StringComparison.Ordinal))
            {
                string category = _sessionState.ForegroundApp?.Category ?? "unknown";
                AppAwareRemarkGate.MarkRemarked(SystemSensor.LocalTime, category);
            }
        }
        catch
        {
            // Native cooldown tracking is optional.
        }
        _proactiveRequest = true;
        _activeProactiveKind = kind;
        _currentUserText = ParticipationPrompt.UserText;
        // A proposed topic is not an event she has already participated in.
        // Actual speech records its own AssistantReply; quiet consumes the
        // session-local candidate only after a successful explicit decision.
        NativeEventLog.Trace("meta.vision.deferred", "mod", "system",
            "trial permits model-requested capture only in player dialogue");
        string proactiveExtra = ParticipationPrompt.BuildContextWithPacing(reason, _proactivePacing.ConsecutiveUnansweredSpeeches);
        if (_config.SensoryEnabled && _sensoryClient.Latest is { Available: true } sensorySnap && sensorySnap.TailText.Length > 0)
        {
            proactiveExtra += System.Environment.NewLine + sensorySnap.TailText;
            if (sensorySnap.MaxBufferSeq > 0)
                _sensoryClient.AcknowledgeConsumed(_config, sensorySnap.MaxBufferSeq);
        }
        SubmitRequest(
            _currentUserText,
            extraContext: proactiveExtra, userEventId: "");
    }

    // Runs before the empty-result fallback and all action / note / memory effects.
    // An accepted explicit empty text (or silent <note> only) is a choice only on the proactive path.
    [HideFromIl2Cpp]
    internal bool TrySettleProactiveSilence(ApiResult result)
    {
        bool quietOrPaperOnly = result.ExplicitEmptyText || result.NoteSupplement.Count > 0;
        if (!_proactiveRequest || result.DialogueRequest != null
            || !quietOrPaperOnly || !string.IsNullOrWhiteSpace(result.Text)
            || result.Error.Length != 0 || result.Response.Outcome != "accepted")
            return false;

        float now = UnityEngine.Time.unscaledTime;
        string kind = result.ProactiveCandidateKind.Length > 0 ? result.ProactiveCandidateKind : _activeProactiveKind;
        bool savedNote = false;
        if (result.NoteSupplement.Count > 0 && _config.NoteSupplementEnabled)
        {
            result.TryUseSelection(() =>
            {
                HandleReplyPaper(result);
                savedNote = true;
                return true;
            });
        }

        if (savedNote)
            _proactivePacing.ObserveQuietNote(now, kind, _config);
        else
            _proactivePacing.ObserveQuiet(now, kind, _config);

        _memory.RecordB1ProactiveSupply(result.ProactiveProposalVersions);

        FinishNativeLinkage(result, savedNote ? "quiet_note" : "quiet");
        _requestPending = false;
        _proactiveRequest = false;
        _activeProactiveKind = "";
        _currentUserText = "";
        _sessionState.PendingIntent = "";
        _sessionState.MarkConversationStarted();
        result.DeliveryOutcome = savedNote ? "quiet_note" : "quiet";
        NativeEventLog.Trace(savedNote ? "meta.proactive.quiet_note" : "meta.proactive.quiet", "mod", "system",
            savedNote ? "proactive candidate settled via silent paper note" : "proactive candidate considered; no speech or side effects",
            "request_id=" + result.RequestId + ";attempt_id=" + result.Response.AttemptId + ";kind=" + kind);
        return true;
    }
}

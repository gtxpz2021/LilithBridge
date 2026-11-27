using System;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

/// <summary>
/// Adaptive Token Bucket for Staged Afterbeat:
/// - Starts with 1 available token so early-session banter can spark naturally without cold-start lockout;
/// - Consumes the token ONLY when the staged afterbeat bubble actually appears on screen (cancelled waits cost 0);
/// - Refills after AfterbeatMinTurnGap player turns AND cooldown seconds (longer if the last afterbeat went unanswered).
/// </summary>
internal sealed class AfterbeatGateState
{
    private const float AnswerWindowSeconds = 120f;
    private bool _hasToken = true;
    private int _turnsSinceConsumed;
    private float _lastConsumedAt = float.NegativeInfinity;
    private float _lastConsumedWatchAt = float.NegativeInfinity;
    private bool _waitingForAnswer;
    private bool _lastWasAnswered = true;

    internal bool HasToken => _hasToken;
    internal int TurnsSinceConsumed => _turnsSinceConsumed;
    internal bool LastWasAnswered => _lastWasAnswered;

    internal void ObservePlayerSubmission(float now, BridgeConfig config)
    {
        if (_waitingForAnswer)
        {
            _lastWasAnswered = now - _lastConsumedWatchAt <= AnswerWindowSeconds;
            _waitingForAnswer = false;
        }
        if (!_hasToken)
            _turnsSinceConsumed++;
        TryRefill(now, config);
    }

    internal bool CanStage(float now, BridgeConfig config)
    {
        if (!config.AfterbeatEnabled)
            return false;
        TryRefill(now, config);
        return _hasToken;
    }

    internal void ConsumeOnDisplayed(float now)
    {
        _hasToken = false;
        _turnsSinceConsumed = 0;
        _lastConsumedAt = now;
        _lastConsumedWatchAt = now;
        _waitingForAnswer = true;
        _lastWasAnswered = false;
    }

    internal void TryRefill(float now, BridgeConfig config)
    {
        if (_hasToken)
            return;
        if (_waitingForAnswer && now - _lastConsumedWatchAt > AnswerWindowSeconds)
        {
            _lastWasAnswered = false;
            _waitingForAnswer = false;
        }
        if (_turnsSinceConsumed < Math.Max(0, config.AfterbeatMinTurnGap))
            return;
        float requiredCooldown = _waitingForAnswer
            ? config.AfterbeatCooldownSeconds
            : (_lastWasAnswered ? config.AfterbeatCooldownSeconds : config.AfterbeatUnansweredCooldownSeconds);
        if (now - _lastConsumedAt >= Math.Max(0f, requiredCooldown))
            _hasToken = true;
    }
}

internal sealed class StagedAfterbeatItem
{
    internal ApiResult ParentReply { get; init; } = null!;
    internal AfterbeatPlan Plan { get; init; } = null!;
    internal ApiResult PreparedReply { get; init; } = null!;
    internal float CountdownStartedAt { get; set; } = -1f;
    internal float FireAt { get; set; } = -1f;
    internal bool TtsPrefetched { get; set; }
}

internal sealed partial class BridgeController
{
    private readonly AfterbeatGateState _afterbeatGate = new();
    private StagedAfterbeatItem? _stagedAfterbeat;
    internal Func<float>? AfterbeatClockForTests;
    private float AfterbeatClock() => AfterbeatClockForTests?.Invoke() ?? Time.unscaledTime;

    [HideFromIl2Cpp]
    internal AfterbeatGateState AfterbeatGate => _afterbeatGate;

    [HideFromIl2Cpp]
    internal bool HasStagedAfterbeat => _stagedAfterbeat != null;

    [HideFromIl2Cpp]
    internal bool TryStageAfterbeat(ApiResult parentReply, AfterbeatPlan plan, int shownMainChunks)
    {
        if (shownMainChunks <= 0 || string.IsNullOrWhiteSpace(plan.Text)
            || parentReply.IsAfterbeat || parentReply.IsProactiveTurn
            || parentReply.IsLocalFallback || parentReply.IsMemoryOperation)
            return false;
        float now = AfterbeatClock();
        if (!_afterbeatGate.CanStage(now, _config))
        {
            NativeEventLog.Trace("ai.afterbeat.throttled", "mod", "system",
                "afterbeat skipped by adaptive token bucket",
                "request_id=" + parentReply.RequestId + ";turns=" + _afterbeatGate.TurnsSinceConsumed);
            return false;
        }
        if (!MatchesConversationGeneration(parentReply)
            || !parentReply.RequestSourcesPermitted(includeChatHistory: false))
            return false;

        CancelStagedAfterbeat("replaced_by_new_afterbeat");

        var prepared = new ApiResult
        {
            Text = plan.Text,
            Action = plan.Action,
            Emotion = string.IsNullOrWhiteSpace(plan.Emotion) ? "neutral" : plan.Emotion,
            IsAfterbeat = true,
            AfterbeatParentRequestId = parentReply.RequestId,
            AfterbeatChunkOffset = shownMainChunks
        };
        string ownRequestId = prepared.RequestId;
        prepared.CopyRequestFrom(parentReply);
        prepared.RequestId = ownRequestId;
        prepared.DialogueRequest = null;
        prepared.ConversationGeneration = parentReply.ConversationGeneration;
        prepared.Response.Outcome = "accepted";
        prepared.Response.BodyComplete = true;
        prepared.Response.FinishReason = "stop";
        if (!string.IsNullOrWhiteSpace(prepared.Action))
            PersonaStateEngine.RepairsAction(prepared, _sessionState, _config, out _);

        _stagedAfterbeat = new StagedAfterbeatItem
        {
            ParentReply = parentReply,
            Plan = plan,
            PreparedReply = prepared
        };
        NativeEventLog.Trace("ai.afterbeat.staged", "mod", "lilith",
            "afterbeat staged behind primary bubble completion",
            "parent_request_id=" + parentReply.RequestId + ";afterbeat_request_id=" + ownRequestId
            + ";raw_delay=" + plan.DelaySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    [HideFromIl2Cpp]
    internal void CancelStagedAfterbeat(string reason)
    {
        if (_stagedAfterbeat is not { } staged)
            return;
        _stagedAfterbeat = null;
        if (staged.TtsPrefetched)
            CancelTtsSpeech("afterbeat_" + reason);
        NativeEventLog.Trace("ai.afterbeat.cancelled", "mod", "system",
            "staged afterbeat swallowed locally before display",
            "parent_request_id=" + staged.ParentReply.RequestId + ";reason=" + reason);
    }

    [HideFromIl2Cpp]
    internal void TickStagedAfterbeat()
    {
        if (_stagedAfterbeat is not { } staged)
            return;

        if (ShouldCancelStagedAfterbeat(staged, out string cancelReason))
        {
            CancelStagedAfterbeat(cancelReason);
            return;
        }

        bool primaryStillActive = _aiSpeechActive
            || NativeBubblePresenter.HasPendingReply
            || _ttsPlayer.IsPlaying
            || (!staged.TtsPrefetched && _ttsCoordinator.HasActiveOrQueued);
        if (primaryStillActive)
        {
            staged.CountdownStartedAt = -1f;
            staged.FireAt = -1f;
            return;
        }

        float now = AfterbeatClock();
        if (staged.CountdownStartedAt < 0f)
        {
            float clampedDelay = Math.Clamp(staged.Plan.DelaySeconds,
                _config.AfterbeatMinDelaySeconds, _config.AfterbeatMaxDelaySeconds);
            staged.CountdownStartedAt = now;
            staged.FireAt = now + clampedDelay;
            ReplyDeliveryRecord delivery = staged.PreparedReply.Delivery ??= new ReplyDeliveryRecord
            {
                RequestId = staged.PreparedReply.RequestId,
                AttemptId = staged.PreparedReply.Response.AttemptId,
                UserEventId = staged.PreparedReply.SourceUserEventId
            };
            BubbleDisplayPlan displayPlan = delivery.PrepareFinalDisplayPlan(
                staged.PreparedReply.PublicTextForDisplay,
                BubbleChunks.BuildPlan(staged.PreparedReply.PublicTextForDisplay, new[] { staged.PreparedReply.PublicTextForDisplay }));
            PrefetchTtsForReply(staged.PreparedReply, displayPlan, 0);
            staged.TtsPrefetched = true;
            NativeEventLog.Trace("ai.afterbeat.armed", "mod", "lilith",
                "primary bubble dismissed; afterbeat countdown started",
                "parent_request_id=" + staged.ParentReply.RequestId
                + ";delay_s=" + clampedDelay.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (now < staged.FireAt)
            return;

        _stagedAfterbeat = null;
        QueueGeneratedReply(staged.PreparedReply);
        NativeEventLog.Trace("ai.afterbeat.dispatched", "mod", "lilith",
            "staged afterbeat queued for display",
            "parent_request_id=" + staged.ParentReply.RequestId
            + ";afterbeat_request_id=" + staged.PreparedReply.RequestId);
    }

    private bool ShouldCancelStagedAfterbeat(StagedAfterbeatItem staged, out string reason)
    {
        if (!_config.AfterbeatEnabled) { reason = "disabled"; return true; }
        if (!MatchesConversationGeneration(staged.PreparedReply)) { reason = "conversation_generation_changed"; return true; }
        if (_listening || _inputField.IsOpen || _inputField.UiConsumesKeyboard
            || !string.IsNullOrEmpty(_inputField.Text)) { reason = "player_input_active"; return true; }
        if (IsSttActive) { reason = "stt_active"; return true; }
        if (_requestPending || _queuedReply != null || _storyRecallInProgress || _memoryRecallInProgress
            || SharedActivityRunning || SharedActivityUiBusy || _b1SelectionWindow.IsOpen || _historyWindow.IsOpen)
        { reason = "controller_busy"; return true; }
        if (_lastDrag || _leftDragging || _sessionState.Native.IsDragging || _sessionState.Native.IsInteracting)
        { reason = "character_interaction"; return true; }
        if (NativeStateInspector.IsSleeping || NativeStateInspector.IsNativeQuietForMod()
            || HasActiveNativeStoryForProactive() || ActionDirector.NativeBusy(_sessionState))
        { reason = "native_busy_or_quiet"; return true; }
        if (_config.SensoryEnabled && _sensoryClient.Latest is { Available: true } s
            && string.Equals(s.InterruptCost, "high_focus", StringComparison.Ordinal))
        { reason = "sensory_high_focus"; return true; }
        if (!staged.ParentReply.RequestSourcesPermitted(includeChatHistory: false))
        { reason = "source_permission_changed"; return true; }
        reason = "";
        return false;
    }
}

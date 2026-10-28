using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LilithBridge;

internal static class NativeBubblePresenter
{
    private static readonly Queue<string> Pending = new();
    private static ApiResult? _activeReply;
    private static BubbleDisplayPlan? _activePlan;
    private static LilithActionType? _activeAction;
    private static string _activeEmotion = "";
    private static string _activeVoice = "";
    private static int _chunkIndex;
    private static string _lastShown = "";
    private static float _nextChunkAt;
    private static bool _retrying;
    private static float _retryUntil;
    private const float RetryIntervalSeconds = 0.5f;
    private const float RetryWindowSeconds = 3f;
    // The delivery record lives on the result itself: the generated full text
    // stays separate from the actually displayed prefix. The presenter fills
    // it; the controller consumes it for history and settlement receipts.
    private static ReplyDeliveryRecord EnsureDelivery(ApiResult reply) =>
        reply.Delivery ??= new ReplyDeliveryRecord
        {
            RequestId = reply.RequestId,
            AttemptId = reply.Response.AttemptId,
            UserEventId = reply.SourceUserEventId
        };
    public static bool HasPendingReply => _activeReply != null;

    public static void ObserveGenerated(ApiResult reply)
    {
        if (reply.Error.Length > 0 || reply.Response.Outcome == "cancelled"
            || reply.DialogueRequest?.IsCancelled == true || MemoryStore.Clean(reply.Text).Length == 0)
            return;
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        if (delivery.GeneratedReceivedAtUtc != null) return;
        delivery.MarkGenerated(MemoryStore.Clean(reply.Text));
        reply.DialogueRequest?.WaitStatus.MarkGenerationReceived();
        TraceDelivery("ai.generation_received", reply, delivery, "", "");
    }

    // Final controller text boundary. The controller calls this from
    // ApplyAiResult after the thought/claim/coherence rewrites and before the
    // display queue, so a repaired or replaced text and its deterministic
    // segment range are fixed together. The earlier generation stays only as
    // the record's diagnostic OriginalGeneratedText and never contributes
    // character offsets to the delivered range.
    public static void SyncGeneratedDelivery(ApiResult reply)
    {
        if (reply.DeliveryOutcome.Length > 0)
            return;
        if (BridgeController.Instance?.NativeLinkageAllows(reply) == false) return;
        string text = MemoryStore.Clean(reply.Text);
        if (text.Length == 0)
            return;
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        delivery.PrepareFinalDisplayPlan(text);
    }

    public static void Show(ApiResult reply)
    {
        if (BridgeController.Instance?.NativeLinkageAllows(reply) == false) return;
        string text = MemoryStore.Clean(reply.Text);
        if (text.Length == 0)
            return;
        if (ReferenceEquals(_activeReply, reply) || reply.DeliveryOutcome.Length > 0)
            return;
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        BubbleDisplayPlan plan = delivery.PrepareFinalDisplayPlan(text);
        var chunks = plan.Chunks;
        // A cancelled owner can outlive its callback or display retry. Reject it
        // before replacing another reply or executing its first-bubble action.
        if (reply.DialogueRequest?.IsCancelled == true)
        {
            reply.DeliveryOutcome = "cancelled";
            delivery.CancelBeforeDisplay(text, chunks.Count,
                "cancelled", "request_cancelled_before_display");
            TraceDelivery("ai.delivery_terminal", reply, delivery,
                "cancelled", "request_cancelled_before_display", pendingChunks: chunks.Count);
            BridgeController.Instance?.OnAiDeliveryEnded(reply, "cancelled");
            return;
        }
        ClearPending("replaced");

        _activeReply = reply;
        _activePlan = plan;
        _activeAction = ActionCatalog.TryParseAllowedAction(reply.Action, out LilithActionType parsedAction)
            ? parsedAction
            : null;
        if (!string.IsNullOrWhiteSpace(reply.Action) && !_activeAction.HasValue)
        {
            NativeEventLog.Trace("action.exposed_rejected", "mod", "lilith",
                reply.Action, "action not in the exposed/playable whitelist");
        }
        if (_activeAction.HasValue && !ActionCapability.IsPoseSafe(_activeAction.Value, null))
        {
            NativeEventLog.Trace("action.capability_stripped", "mod", "lilith",
                _activeAction.Value.ToString(), "available_action_list");
            _activeAction = null;
        }
        if (_activeAction.HasValue)
        {
            LilithActionType? poseResolved = ActionExecutor.ResolveActionForPose(_activeAction);
            if (poseResolved.HasValue)
            {
                if (poseResolved.Value != _activeAction.Value)
                {
                    NativeEventLog.Trace("action.pose_resolved", "mod", "lilith",
                        _activeAction.Value.ToString(), "resolved=" + poseResolved.Value);
                }
                _activeAction = poseResolved;
            }
            else
            {
                NativeEventLog.Trace("action.pose_blocked", "mod", "lilith",
                    _activeAction.Value.ToString(), "no pose-safe fallback");
                _activeAction = null;
            }
        }
        if (_activeAction.HasValue && !ActionExecutor.HasNativeAnimation(_activeAction.Value))
        {
            NativeEventLog.Trace("action.availability_skip", "mod", "lilith",
                _activeAction.Value.ToString(),
                "native HasLilithActionAnimation returned false");
            _activeAction = null;
        }
        _activeEmotion = ActionCatalog.NormalizeEmotion(reply.Emotion);
        _activeVoice = reply.Voice;
        Pending.Clear();
        _nextChunkAt = 0f;
        _lastShown = "";
        _chunkIndex = 0;

        if (chunks.Count == 0)
            return;
        foreach (ReplyDeliveryChunk chunk in chunks)
            Pending.Enqueue(chunk.Text);
        delivery.MarkQueued(plan);
        TraceDelivery("ai.delivery_queued", reply, delivery, "", "");
        if (!ShowNextChunk(true))
            ScheduleRetry();
    }

    public static bool OnChunkFinished()
    {
        if (StopCancelledOwner()) return false;
        if (_retrying)
            return _chunkIndex > 0 && Pending.Count > 0;
        if (Pending.Count > 0)
        {
            // Reading rhythm: a bubble that stopped mid-sentence is followed almost
            // immediately; one that ended a sentence gets a fuller pause.
            _nextChunkAt = Time.unscaledTime + BubbleChunks.PauseAfter(_lastShown) / 1000f;
            return true;
        }
        ClearActive();
        return false;
    }

    /// <summary>
    /// Called every frame by the bridge; shows the queued bubble once the pause
    /// decided in <see cref="OnChunkFinished"/> has elapsed.
    /// </summary>
    public static void Tick()
    {
        if (StopCancelledOwner()) return;
        if (Pending.Count == 0 || _nextChunkAt <= 0f)
            return;
        if (_retrying && Time.unscaledTime >= _retryUntil)
        {
            ClearPending("display_unavailable");
            return;
        }
        if (Time.unscaledTime < _nextChunkAt)
            return;
        if (_retrying && BridgeController.Instance?.CanResumeAiDelivery() == false)
            return;
        _nextChunkAt = 0f;
        if (!ShowNextChunk(_chunkIndex == 0))
            ScheduleRetry();
    }

    public static void ClearPending(string outcome = "cancelled", string reason = "")
    {
        int pendingChunks = Pending.Count;
        Pending.Clear();
        _nextChunkAt = 0f;
        _lastShown = "";
        EndDelivery(outcome, reason, pendingChunks);
    }

    // Marks a completed generation as waiting for its playback window. The
    // controller calls this before the presenter owns a display slot, so the
    // record's queue moment survives a later display or an early cancellation.
    // It repeats the final text sync defensively: queueing and display must
    // always describe the same text the player will actually receive.
    public static void MarkQueuedForDisplay(ApiResult reply)
    {
        if (reply.DeliveryOutcome.Length > 0)
            return;
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        string text = MemoryStore.Clean(reply.Text);
        delivery.MarkQueued(delivery.PrepareFinalDisplayPlan(text));
    }

    // Ends a generated reply that never entered the display queue (for example a
    // reply waiting in the controller for its playback window). No history,
    // experience, reflection/day or other "already told" write may happen.
    public static void CancelUndispatched(ApiResult reply, string outcome, string reason)
    {
        if (reply.DeliveryOutcome.Length > 0)
            return;
        if (ReferenceEquals(_activeReply, reply))
            return;
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        string text = MemoryStore.Clean(reply.Text);
        delivery.CancelBeforeDisplay(text, SplitNaturalChunks(text).Count, outcome, reason);
        reply.DeliveryOutcome = outcome;
        TraceDelivery("ai.delivery_terminal", reply, delivery, outcome, reason,
            pendingChunks: delivery.AbandonedChunks);
    }

    private static bool StopCancelledOwner()
    {
        if (_activeReply != null && BridgeController.Instance?.NativeLinkageAllows(_activeReply) == false)
        { ClearPending("cancelled", "native_linkage_invalidated"); return true; }
        if (_activeReply?.DialogueRequest?.IsCancelled != true) return false;
        ClearPending("cancelled", "request_cancelled");
        return true;
    }

    private static void ScheduleRetry()
    {
        if (_activeReply == null || Pending.Count == 0) return;
        if (!_retrying)
        {
            _retrying = true;
            _retryUntil = Time.unscaledTime + RetryWindowSeconds;
            NativeEventLog.Trace("native.bubble.retry", "mod", "lilith", "display temporarily unavailable",
                "request_id=" + _activeReply?.RequestId + ";attempt_id=" + _activeReply?.Response.AttemptId
                + ";chunk=" + _chunkIndex);
        }
        _nextChunkAt = Time.unscaledTime + RetryIntervalSeconds;
    }

    private static bool ShowNextChunk(bool first)
    {
        if (StopCancelledOwner()) return false;
        if (Pending.Count == 0)
            return false;
        string text = Pending.Peek();
        string emotion = first ? _activeEmotion : "";
        LilithActionType? action = first ? _activeAction : null;
        // Proportional rather than "at least six seconds", and deliberately short:
        // within one reply the previous bubble should hand over promptly, and a
        // short line needs far less time than a long one.
        float duration = Math.Clamp(1.2f + text.Length * 0.045f, 1.5f, 6f);
        // The original Say overload accepts an action type but does not reliably
        // play the native animation. We keep the motion in ActionExecutor so a
        // single <action> actually reaches controller.PlayLilithAction.
        LilithActionType? nativeAction =
            action is { } resolved
                && resolved is not (LilithActionType.None or LilithActionType.Idle)
                ? action
                : null;
        bool displayed = false;

        try
        {
            DialogueManager? manager = DialogueManager.instance;
            if (manager != null)
            {
                displayed = manager.Say(
                    text,
                    LilithActionType.None,
                    emotion,
                    duration,
                    0,
                    true);
            }
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Native bubble Say failed: " + error.Message);
        }

        if (displayed)
        {
            Pending.Dequeue();
            _retrying = false;
            _lastShown = text;
            NotifyShown(text,first,action,nativeAction);
            return true;
        }

        try
        {
            DialogueBubbleUI? bubble = UnityEngine.Object.FindObjectOfType<DialogueBubbleUI>(true);
            MethodInfo? show = bubble?.GetType().GetMethod(
                "ShowNode",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bubble != null && show != null)
            {
                var node = new DialogueNode
                {
                    id = -100000 - (text.GetHashCode() & 0x7FFFFFFF) % 100000,
                    speaker = "Lilith",
                    text = text,
                    emotion = emotion,
                    actionType = LilithActionType.None,
                    duration = duration,
                    soundId = "",
                    textIsFinal = true
                };
                object? result = show.Invoke(bubble, new object[] { node });
                displayed = result is not bool success || success;
            }
        }

        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("Native bubble ShowNode failed: " + error.Message);
        }

        if (displayed)
        {
            Pending.Dequeue();
            _retrying = false;
            _lastShown = text;
            NotifyShown(text,first,action,nativeAction);
            return true;
        }

        NativeEventLog.Trace("native.bubble.fallback", "mod", "lilith", text,
            "no native bubble available");
        return false;
    }

    private static void NotifyShown(string text, bool first, LilithActionType? action, LilithActionType? nativeAction)
    {
        // Display has already succeeded. A bookkeeping or animation failure
        // must never send the same visible chunk through a second display path.
        int index = _chunkIndex++;
        ApiResult? shownReply = _activeReply;
        ReplyDeliveryRecord? delivery = null;
        if (shownReply != null)
        {
            delivery = EnsureDelivery(shownReply);
            delivery.MarkShown(index, text, _activePlan?.Version ?? "");
            shownReply.DialogueRequest?.WaitStatus.MarkFirstShown();
        }
        try { BridgeController.Instance?.OnAiBubbleShown(text,_activeReply,index,Pending.Count == 0); }
        catch (Exception error) { Plugin.PluginLog.LogWarning("Bubble delivery receipt failed: " + error.Message); }
        if (shownReply != null && delivery != null)
            TraceDelivery("ai.delivery_chunk", shownReply, delivery, "", "", index, text.Length);
        try
        {
            ActionPathDiagnostic.TracePlayback(nativeAction);
            if (first && _activeReply != null)
                ActionExecutor.Execute(_activeReply,action:action,alreadyHandledByNativeSay:false);
        }
        catch (Exception error) { Plugin.PluginLog.LogWarning("Bubble action failed: " + error.Message); }
        NativeEventLog.Trace("native.bubble", "mod", "lilith", text,"shown;first=" + first + ";chunk=" + index);
        if (Pending.Count == 0)
            EndDelivery("completed");
    }

    private static List<string> SplitNaturalChunks(string text)
    {
        // Chunking lives in BubbleChunks so it can be tested offline; the rules
        // (single bubble ≤28 chars, merge to ~24, fold a <10-char tail back) keep
        // each bubble close to the original game's line length while never
        // dropping characters.
        return BubbleChunks.Split(text);
    }

    private static void ClearActive()
    {
        _activeReply = null;
        _activePlan = null;
        _activeAction = null;
        _activeEmotion = "";
        _activeVoice = "";
        _retrying = false;
        _retryUntil = 0f;
    }

    private static void EndDelivery(string outcome, string reason = "", int? abandonedChunks = null)
    {
        ApiResult? reply = _activeReply;
        int pendingChunks = abandonedChunks ?? Pending.Count;
        // Clear before notifying so re-entrant cancellation cannot settle twice.
        ClearActive();
        if (reply == null || reply.DeliveryOutcome.Length > 0)
            return;
        reply.DeliveryOutcome = outcome;
        string receiptReason = reason.Length > 0
            ? reason
            : outcome == "cancelled" ? "unspecified" : outcome;
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        int pendingChars = outcome == "completed"
            ? 0
            : Math.Max(0, (delivery.DisplayPlan?.RenderedText.Length ?? delivery.GeneratedChars) - delivery.DeliveredChars);
        delivery.Complete(outcome, receiptReason, pendingChunks, pendingChars);
        TraceDelivery("ai.delivery_terminal", reply, delivery, outcome, receiptReason,
            pendingChunks: pendingChunks);
        try { BridgeController.Instance?.OnAiDeliveryEnded(reply, outcome); }
        catch (Exception error) { Plugin.PluginLog.LogWarning("Bubble settlement failed: " + error.Message); }
    }

    private static void TraceDelivery(string kind, ApiResult reply, ReplyDeliveryRecord delivery,
        string outcome, string reason, int? chunkIndex = null, int? chunkChars = null, int? pendingChunks = null)
    {
        // Receipt times describe this client only. The non-streaming API does not
        // expose first-token time, server queue time or a generation-time split.
        RequestWaitStatus? wait = reply.DialogueRequest?.WaitStatus;
        NativeEventLog.Trace(kind, "mod", "system", "reply generation/display receipt",
            JsonSerializer.Serialize(new
            {
                request_id = reply.RequestId, attempt_id = reply.Response.AttemptId,
                outcome, reason,
                delivery_outcome = delivery.Outcome, delivery_reason = delivery.Reason,
                generated_received_at_utc = delivery.GeneratedReceivedAtUtc,
                queued_at_utc = delivery.QueuedAtUtc,
                first_shown_at_utc = delivery.FirstShownAtUtc,
                completed_at_utc = delivery.CompletedAtUtc,
                event_at_utc = DateTimeOffset.UtcNow,
                received_to_event_ms = delivery.GeneratedReceivedAtUtc == null ? (long?)null
                    : Math.Max(0, Environment.TickCount64 - delivery.GeneratedReceivedAtTicks),
                queued_to_event_ms = delivery.QueuedAtUtc == null ? (long?)null
                    : Math.Max(0, Environment.TickCount64 - delivery.QueuedAtTicks),
                client_wait_started_at_utc = wait?.StartedAtUtc,
                client_wait_completed_at_utc = wait?.CompletedAtUtc,
                client_generation_received_at_utc = wait?.GenerationReceivedAtUtc,
                client_first_shown_at_utc = wait?.FirstShownAtUtc,
                client_wait_ms = wait?.ElapsedMilliseconds, api_latency_ms = reply.LatencyMs,
                generated_chars = delivery.GeneratedChars, delivered_chars = reply.DeliveredText.Length,
                original_generated_chars = delivery.OriginalGeneratedText.Length,
                generated_text_replaced = delivery.OriginalGeneratedText.Length > 0,
                shown_chars = delivery.DeliveredChars, shown_chunks = delivery.ShownChunks.Count,
                total_chunks = delivery.TotalChunks,
                source_user_event_id = delivery.UserEventId,
                final_plan_chunks = delivery.FinalDisplayPlan.Count,
                final_plan_version = delivery.DisplayPlan?.Version,
                final_plan_chars = delivery.DisplayPlan?.RenderedText.Length,
                declared_whitespace_omissions = delivery.DisplayPlan?.WhitespaceOmissions.Sum(o => o.GeneratedChars),
                final_plan_valid = delivery.HasValidDisplayPlan,
                fully_delivered = delivery.IsFullyDelivered,
                pending_chunks = pendingChunks ?? delivery.AbandonedChunks,
                abandoned_chunks = delivery.AbandonedChunks, abandoned_chars = delivery.AbandonedChars,
                chunk_index = chunkIndex, chunk_chars = chunkChars
            }));
    }
}

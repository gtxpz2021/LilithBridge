using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LilithBridge;

internal static partial class NativeBubblePresenter
{
    private static readonly Queue<string> Pending = new();
    private static ApiResult? _activeReply;
    private static BubbleDisplayPlan? _activePlan;
    private static LilithActionType? _activeAction;
    private static string _activeEmotion = "";
    private static string _activeVoice = "";
    private static int _chunkIndex,_startChunkIndex;
    private static string _lastShown = "";
    private static float _nextChunkAt;
    private static bool _retrying;
    private static float _retryUntil;
    private static float _editingPausedAt = -1f;
    private sealed record PendingAppearance(ApiResult Reply, int Index, string Text, bool First,
        LilithActionType? Action, LilithActionType? NativeAction, NativeAppearanceWait Wait);
    private static PendingAppearance? _appearance;
    internal static Func<(double Time, int Frame)>? AppearanceClockForTests { get; set; }
    private static (double Time, int Frame) AppearanceClock() => AppearanceClockForTests?.Invoke()
        ?? (Time.unscaledTime, Time.frameCount);
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
        // Capture the actual received public answer before Clean or local
        // rewrites. A cancelled owner can still have received an answer; that
        // does not make any of its body a displayed history event.
        if (reply.Error.Length == 0 && !reply.IsLocalFallback
            && reply.Response.Outcome is "accepted" or "accepted_without_finish_reason")
            BridgeController.Instance?.CaptureGeneratedPublicReply(reply);
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
        if(!PublicFinalHandoff.Prepare(reply,text,delivery))
        {CancelUndispatched(reply,"public_prefix_changed","local_final_text_differs_from_streamed_prefix");return;}
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
        if(reply.DialogueRequest?.IsCancelled==true)
        {CancelUndispatched(reply,"cancelled","request_cancelled_before_stream_handoff");return;}
        if(!PublicFinalHandoff.Prepare(reply,text,delivery)||!PublicFinalHandoff.Import(reply,delivery))
        {CancelUndispatched(reply,"public_prefix_changed","streamed_prefix_handoff_not_verified");return;}
        if(BridgeController.Instance?.ArchiveFinalStreamPrefix(reply)==false)
        {CancelUndispatched(reply,"source_changed","streamed_prefix_archive_source_changed");return;}
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
        if(NativeActionPreparationForTests is {} prepareAction)_activeAction=prepareAction(_activeAction);
        else
        {
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
        }
        _activeEmotion = ActionCatalog.NormalizeEmotion(reply.Emotion);
        _activeVoice = reply.Voice;
        Pending.Clear();
        _nextChunkAt = 0f;
        _lastShown = "";
        _chunkIndex = _startChunkIndex = delivery.ShownChunks.Count;

        if (chunks.Count == 0)
            return;
        foreach (ReplyDeliveryChunk chunk in chunks.Skip(_startChunkIndex))
            Pending.Enqueue(chunk.Text);
        delivery.MarkQueued(plan);
        TraceDelivery("ai.delivery_queued", reply, delivery, "", "");
        if(Pending.Count==0){CompleteWithoutNewBubble(reply);return;}
        if (!ShowNextChunk(true) && ReferenceEquals(_activeReply, reply))
            ScheduleRetry();
    }

    public static bool OnChunkFinished()
    {
        if (StopCancelledOwner()) return false;
        if (_appearance is { } unseen)
        {
            _appearance = null;
            EndUnknownDisplay(unseen.Reply, unseen.Index, unseen.Text, "appearance",
                "native_ended_without_observed_appearance");
            return false;
        }
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

    // Freeze the retry clock while an unsubmitted draft owns input.
    public static void PauseForEditing()
    {
        if (_activeReply != null && Pending.Count > 0 && _editingPausedAt < 0f)
            _editingPausedAt = Time.unscaledTime;
    }

    /// <summary>Advance the next bubble after its reading pause and input gates.</summary>
    public static void Tick()
    {
        if (BridgeController.Instance?.CancelDeliveryForSleep() == true) return;
        if (StopCancelledOwner()) return;
        if (_appearance != null) { ResolveAppearance(); return; }
        if (BridgeController.Instance?.AiDeliveryEditing == true)
        { PauseForEditing(); return; }
        if (_editingPausedAt >= 0f)
        {
            if (_retrying) _retryUntil += Math.Max(0f, Time.unscaledTime - _editingPausedAt);
            _editingPausedAt = -1f;
        }
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
        ApiResult? advancing = _activeReply;
        if (!ShowNextChunk(_chunkIndex == _startChunkIndex) && ReferenceEquals(_activeReply, advancing))
            ScheduleRetry();
    }

    public static void ClearPending(string outcome = "cancelled", string reason = "")
    {
        if (_appearance is { } accepted)
        {
            _appearance = null;
            EnsureDelivery(accepted.Reply).MarkDisplayUnknown(accepted.Index, accepted.Text,
                "accepted_without_observed_appearance");
        }
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
        if(!PublicFinalHandoff.Prepare(reply,text,delivery))
        {CancelUndispatched(reply,"public_prefix_changed","local_final_text_differs_from_streamed_prefix");return;}
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
        BridgeController.Instance?.RecordGeneratedPublicEnd(reply, outcome);
        TraceDelivery("ai.delivery_terminal", reply, delivery, outcome, reason,
            pendingChunks: delivery.AbandonedChunks);
    }

    private static bool StopCancelledOwner()
    {
        // Cancellation is already authoritative; do not reconstruct source
        // history for an owner that can no longer present anything.
        if (_activeReply?.DialogueRequest?.IsCancelled == true)
        { ClearPending("cancelled", "request_cancelled"); return true; }
        // Show checked full sources before accepting this completed reply. Keep
        // its ordinary speech natural: display ticks and subsequent chunks only
        // check live owner/explicit cancellation, without rereading old review
        // material. Generated-but-unshown text remains in the existing archive
        // when an explicit interruption clears the pending display queue.
        if (_activeReply != null && BridgeController.Instance?.NativePlaybackAllows(_activeReply) == false)
        { ClearPending("cancelled", "playback_owner_invalidated"); return true; }
        return false;
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
        if (_appearance != null) return true;
        if (Pending.Count == 0)
            return false;
        ApiResult? displayingReply = _activeReply;
        int displayingIndex = _chunkIndex;
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
        NativeBubbleAttempt attempt = default!;
        bool Dispatch() { attempt = TryNativeBubble(new(text, emotion, duration)); return true; }
        if (!(displayingReply?.TryUseSelection(Dispatch) ?? Dispatch()))
        { ClearPending("cancelled", "selection_changed"); return false; }
        if (!ReferenceEquals(_activeReply, displayingReply)) return true;
        if (attempt.Outcome == WorkDisplayOutcome.Unknown && displayingReply != null)
        { EndUnknownDisplay(displayingReply, displayingIndex, text, attempt.Endpoint); return false; }
        if (attempt.Outcome == WorkDisplayOutcome.Shown)
        {
            Pending.Dequeue(); _retrying = false; _lastShown = text;
            NotifyShown(text, first, action, nativeAction); return true;
        }
        if (attempt.Outcome == WorkDisplayOutcome.Accepted && displayingReply != null)
        {
            var clock = AppearanceClock();
            _retrying = false; _nextChunkAt = 0;
            _appearance = new(displayingReply, displayingIndex, text, first, action, nativeAction,
                new(text, clock.Time, clock.Frame));
            NativeEventLog.Trace("native.bubble.accepted", "mod", "lilith", text,
                "appearance=pending;chunk=" + displayingIndex);
            return true;
        }
        NativeEventLog.Trace("native.bubble.fallback", "mod", "lilith", text, "no native bubble available");
        return false;
    }

    // Cancellation does not lose input or protect a timer window. It samples
    // the current bubble once before ownership changes, then ends normally.
    internal static void FlushObservedAppearance() => ResolveAppearance(atCancellation: true, executeAction: false);
    private static void ResolveAppearance(bool atCancellation = false, bool executeAction = true)
    {
        if (_appearance is not { } pending) return;
        var clock = AppearanceClock();
        var outcome = pending.Wait.Poll(clock.Time, clock.Frame, atCancellation);
        if (outcome == WorkDisplayOutcome.Accepted) return;
        _appearance = null;
        if (!ReferenceEquals(_activeReply, pending.Reply)) return;
        if (outcome != WorkDisplayOutcome.Shown)
        {
            EndUnknownDisplay(pending.Reply, pending.Index, pending.Text, "appearance", "appearance_unconfirmed");
            return;
        }
        Pending.Dequeue(); _lastShown = pending.Text;
        NotifyShown(pending.Text, pending.First, pending.Action, pending.NativeAction, executeAction);
    }

    // Both regular replies and temporary expressions enter the same Say /
    // ShowNode implementation. A test replaces only this native endpoint.
    internal static Func<NativeBubbleRequest, NativeBubbleAttempt>? NativeEndpointForTests { get; set; }
    internal static WorkDisplayOutcome ShowTemporaryBubble(WorkBubble bubble) =>
        TryNativeBubble(new(bubble.Text, "", Math.Clamp(1.2f + bubble.Text.Length * 0.045f, 1.5f, 6f))).Outcome;

    internal static NativeBubbleAttempt TryNativeBubble(NativeBubbleRequest request)
    {
        if (NativeEndpointForTests is { } injected)
        {
            try { return injected(request); }
            catch { return new(WorkDisplayOutcome.Unknown, "injected_native_endpoint"); }
        }
        bool entered = false;
        try
        {
            DialogueManager? manager = DialogueManager.instance;
            if (manager != null)
            {
                entered = true;
                if (manager.Say(request.Text, LilithActionType.None, request.Emotion, request.Duration, 0, true))
                    return new(WorkDisplayOutcome.Accepted, "Say");
            }
        }
        catch (Exception error)
        {
            if (entered) return new(WorkDisplayOutcome.Unknown, "Say");
            Plugin.PluginLog.LogWarning("Native bubble Say unavailable before call: " + error.Message);
        }
        entered = false;
        try
        {
            DialogueBubbleUI? bubble = UnityEngine.Object.FindObjectOfType<DialogueBubbleUI>(true);
            MethodInfo? show = bubble?.GetType().GetMethod("ShowNode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bubble != null && show != null && show.ReturnType == typeof(bool))
            {
                var node = new DialogueNode { id = -100000 - (request.Text.GetHashCode() & 0x7FFFFFFF) % 100000,
                    speaker = "Lilith", text = request.Text, emotion = request.Emotion,
                    actionType = LilithActionType.None, duration = request.Duration, soundId = "", textIsFinal = true };
                entered = true;
                if (show.Invoke(bubble, new object[] { node }) is bool shown && shown)
                    return new(WorkDisplayOutcome.Accepted, "ShowNode");
            }
        }
        catch (Exception error)
        {
            bool targetNotEntered = error is ArgumentException or TargetParameterCountException or TargetException or MemberAccessException;
            if (entered && !targetNotEntered) return new(WorkDisplayOutcome.Unknown, "ShowNode");
            Plugin.PluginLog.LogWarning("Native bubble ShowNode unavailable before call: " + error.Message);
        }
        return new(WorkDisplayOutcome.NotShown, "unavailable");
    }

    private static void EndUnknownDisplay(ApiResult reply, int index, string text, string endpoint, string reason = "")
    {
        if (reason.Length == 0) reason = endpoint + "_exception_after_entry";
        ReplyDeliveryRecord delivery = EnsureDelivery(reply);
        delivery.MarkDisplayUnknown(index, text, endpoint);
        reply.DialogueRequest?.Cancel();
        if (ReferenceEquals(_activeReply, reply))
        {
            try { DialogueManager.instance?.ForceEndDialogue(); } catch { }
            ClearPending("delivery_unknown", reason);
        }
        // Re-entrant handoff may already have ended the old reply. Never clear
        // a different presenter's queue or change its pending request flags.
        TraceDelivery("ai.display_receipt_unknown", reply, delivery, delivery.Outcome,
            reason, index, text.Length);
    }

    private static void NotifyShown(string text, bool first, LilithActionType? action, LilithActionType? nativeAction, bool executeAction = true)
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
            // Record actual appearance even if the following raw archive
            // callback fails. A later exact raw turn upgrades this same receipt.
            BridgeController.Instance?.RecordGeneratedPublicDisplay(shownReply, null, index, text);
            shownReply.DialogueRequest?.WaitStatus.MarkFirstShown();
        }
        try { BridgeController.Instance?.OnAiBubbleShown(text,_activeReply,index,Pending.Count == 0); }
        catch (Exception error) { Plugin.PluginLog.LogWarning("Bubble delivery receipt failed: " + error.Message); }
        if (shownReply != null && delivery != null)
            TraceDelivery("ai.delivery_chunk", shownReply, delivery, "", "", index, text.Length);
        try{ActionPathDiagnostic.TracePlayback(nativeAction);}catch{ }
        if(executeAction&&first&&shownReply!=null)SubmitReplyEffects(shownReply,action,"first_remaining_bubble_appeared");
        NativeEventLog.Trace("native.bubble", "mod", "lilith", text,"shown;first=" + first + ";chunk=" + index);
        if (ReferenceEquals(_activeReply,shownReply)&&Pending.Count == 0)
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
        _appearance = null;
        _activeReply = null;
        _activePlan = null;
        _activeAction = null;
        _activeEmotion = "";
        _activeVoice = "";
        _retrying = false;
        _retryUntil = 0f;
        _editingPausedAt = -1f;
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
        BridgeController.Instance?.RecordGeneratedPublicEnd(reply, outcome);
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
                unknown_chunks = delivery.UnknownChunks, unknown_chars = delivery.UnknownChars,
                unknown_chunk_index = delivery.UnknownChunkIndex, unknown_endpoint = delivery.UnknownEndpoint,
                chunk_index = chunkIndex, chunk_chars = chunkChars
            }));
    }
}

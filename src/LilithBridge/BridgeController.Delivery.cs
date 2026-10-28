using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

// Per-reply delivery record. The generated full text and the actually
// displayed prefix stay separate: only ShownChunks/DeliveredText may enter
// history, the experience archive, reflection/day capture or any other
// "already told" consumer. The record keeps the shown range, the terminal
// outcome/reason and the client-side moments of generation receipt, queueing,
// first display and completion.
internal sealed class ReplyDeliveryRecord
{
    public string RequestId { get; init; } = "";
    public string AttemptId { get; init; } = "";
    public string UserEventId { get; init; } = "";
    // Full generation as received from the client (after the last text
    // boundary), independent of how much of it was displayed.
    public string GeneratedText { get; private set; } = "";
    // Pre-boundary generation kept only as a diagnostic: it never contributes
    // character offsets to the delivery range once the final text is fixed.
    public string OriginalGeneratedText { get; private set; } = "";
    public int GeneratedChars { get; private set; }
    public int TotalChunks { get; private set; }
    public List<ReplyDeliveryChunk> ShownChunks { get; } = new();
    public string DeliveredText { get; private set; } = "";
    public int DeliveredChars => DeliveredText.Length;
    public DateTimeOffset? GeneratedReceivedAtUtc { get; private set; }
    public DateTimeOffset? QueuedAtUtc { get; private set; }
    public DateTimeOffset? FirstShownAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    // Environment.TickCount64 wrappers; kept beside the UTC values so the
    // telemetry can report intervals without parsing timestamps.
    public long GeneratedReceivedAtTicks { get; private set; }
    public long QueuedAtTicks { get; private set; }
    public long FirstShownAtTicks { get; private set; }
    public long CompletedAtTicks { get; private set; }
    public string Outcome { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public int AbandonedChunks { get; private set; }
    public int AbandonedChars { get; private set; }
    public bool IsTerminal => Outcome.Length > 0;
    // This plan belongs to the final display text, not to a physical HTTP or
    // the number of bubbles somebody happened to report.
    public BubbleDisplayPlan? DisplayPlan { get; private set; }
    public IReadOnlyList<ReplyDeliveryChunk> FinalDisplayPlan => DisplayPlan?.Chunks ?? Array.Empty<ReplyDeliveryChunk>();
    public bool HasValidDisplayPlan { get; private set; }
    private bool _shownMatchesPlan = true;
    public bool IsFullyDelivered => Outcome == "completed" && QueuedAtUtc != null
        && HasValidDisplayPlan && _shownMatchesPlan && DisplayPlan != null
        && FinalDisplayPlan.Count > 0 && ShownChunks.Count == FinalDisplayPlan.Count
        && DeliveredText == DisplayPlan.RenderedText;

    private void SetDisplayPlan(BubbleDisplayPlan plan)
    {
        // Re-entering a boundary with the same final plan preserves its version.
        if (DisplayPlan != null && DisplayPlan.GeneratedText == plan.GeneratedText
            && DisplayPlan.IsValid == plan.IsValid
            && DisplayPlan.Chunks.Select(p => p.Text).SequenceEqual(plan.Chunks.Select(p => p.Text)))
        { HasValidDisplayPlan = plan.IsValid; TotalChunks = plan.Chunks.Count; return; }
        if (ShownChunks.Count == 0) _shownMatchesPlan = true;
        else _shownMatchesPlan = false;
        DisplayPlan = plan; HasValidDisplayPlan = plan.IsValid; TotalChunks = plan.Chunks.Count;
    }

    internal BubbleDisplayPlan PrepareFinalDisplayPlan(string text)
    {
        if (IsTerminal) throw new InvalidOperationException("A terminal display plan cannot be replaced");
        UpdateFinalText(text, 0);
        if (DisplayPlan == null) SetDisplayPlan(BubbleChunks.BuildPlan(text));
        return DisplayPlan!;
    }

    internal void MarkGenerated(string text)
    {
        if (GeneratedReceivedAtUtc != null)
            return;
        SyncFinalText(text);
        GeneratedReceivedAtTicks = Environment.TickCount64;
        GeneratedReceivedAtUtc = DateTimeOffset.UtcNow;
    }

    // The final text boundary: the record must describe the text that actually
    // reaches the display queue and its deterministic segments, not an earlier
    // generation that a repair/rewrite replaced. The earlier text keeps a
    // diagnostic identity so old and new character offsets are never mixed.
    internal void SyncFinalText(string text, int totalChunks = 0)
    {
        if (IsTerminal) return;
        UpdateFinalText(text, totalChunks);
        if (totalChunks <= 0) return;
        var plan = PrepareFinalDisplayPlan(text);
        if (plan.Chunks.Count != totalChunks) HasValidDisplayPlan = false;
    }
    internal void SyncFinalText(string text, IReadOnlyList<string> chunks)
    {
        if (IsTerminal) return;
        UpdateFinalText(text, chunks.Count);
        SetDisplayPlan(BubbleChunks.BuildPlan(text, chunks));
    }
    private void UpdateFinalText(string text, int totalChunks)
    {
        if (IsTerminal)
            return;
        if (GeneratedText != text)
        {
            if (OriginalGeneratedText.Length == 0 && GeneratedText.Length > 0)
                OriginalGeneratedText = GeneratedText;
            if (ShownChunks.Count > 0) _shownMatchesPlan = false;
            HasValidDisplayPlan = false; DisplayPlan = null;
            GeneratedText = text;
            GeneratedChars = text.Length;
            if (totalChunks > 0)
                TotalChunks = totalChunks;
            return;
        }
        if (TotalChunks == 0 && totalChunks > 0)
            TotalChunks = totalChunks;
    }

    internal void MarkQueued(string text, int totalChunks)
    {
        SyncFinalText(text, totalChunks); MarkQueuedTime();
    }
    internal void MarkQueued(string text, IReadOnlyList<string> plan)
    {
        SyncFinalText(text, plan); MarkQueuedTime();
    }
    internal void MarkQueued(BubbleDisplayPlan plan)
    {
        if (IsTerminal) return;
        UpdateFinalText(plan.GeneratedText, plan.Chunks.Count); SetDisplayPlan(plan); MarkQueuedTime();
    }
    private void MarkQueuedTime()
    {
        if (QueuedAtUtc != null)
            return;
        QueuedAtTicks = Environment.TickCount64;
        QueuedAtUtc = DateTimeOffset.UtcNow;
        // A local fallback has no separate client generation receipt; its
        // full text becomes available exactly when it is queued.
        if (GeneratedReceivedAtUtc == null)
        {
            GeneratedReceivedAtTicks = QueuedAtTicks;
            GeneratedReceivedAtUtc = QueuedAtUtc;
        }
    }

    internal bool MarkShown(int index, string text)
        => MarkShown(index, text, DisplayPlan?.Version ?? "");

    internal bool MarkShown(int index, string text, string planVersion)
    {
        if (IsTerminal)
            return false;
        bool matches = HasValidDisplayPlan && planVersion == DisplayPlan?.Version && index == ShownChunks.Count
            && index >= 0 && index < FinalDisplayPlan.Count && FinalDisplayPlan[index].Text == text;
        if (!matches) _shownMatchesPlan = false;
        // Keep every actual shown fragment even when its bookkeeping conflicts;
        // an invalid plan must not erase something that really reached the player.
        int start = matches ? FinalDisplayPlan[index].StartChar : -1;
        ShownChunks.Add(new ReplyDeliveryChunk
        {
            Index = index,
            StartChar = start,
            GeneratedChars = matches ? FinalDisplayPlan[index].GeneratedChars : 0,
            DisplayStartChar = matches ? FinalDisplayPlan[index].DisplayStartChar : -1,
            PlanVersion = planVersion,
            Chars = text.Length,
            Text = text
        });
        DeliveredText += text;
        if (FirstShownAtUtc == null)
        {
            FirstShownAtTicks = Environment.TickCount64;
            FirstShownAtUtc = DateTimeOffset.UtcNow;
        }
        return true;
    }

    // A generated reply that never reached the display queue. The whole
    // generated text is recorded as abandoned; nothing may be treated as told.
    internal void CancelBeforeDisplay(string text, int totalChunks, string outcome, string reason)
    {
        MarkGenerated(text);
        // A text replaced after the first generation is still the text this
        // cancellation describes; the original stays diagnostic-only.
        SyncFinalText(text, totalChunks);
        if (TotalChunks == 0)
            TotalChunks = totalChunks;
        Complete(outcome, reason, TotalChunks, Math.Max(0, (DisplayPlan?.RenderedText.Length ?? GeneratedChars) - DeliveredChars));
    }

    internal void Complete(string outcome, string reason, int abandonedChunks, int abandonedChars)
    {
        if (IsTerminal)
            return;
        Outcome = outcome;
        Reason = reason;
        AbandonedChunks = Math.Max(0, abandonedChunks);
        AbandonedChars = Math.Max(0, abandonedChars);
        CompletedAtTicks = Environment.TickCount64;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }

}

// Actual controller methods, also compiled by the offline harness. Native UI and
// the asynchronous extraction boundary are doubles there; history and Persona
// use the product implementations.
internal sealed partial class BridgeController
{
    internal void CancelAiSpeech(string reason = "player_interrupt")
    {
        // A generation that is still waiting for its playback window belongs
        // to the same interruption. Close its delivery record before display so
        // the unseen tail can never arrive late.
        CancelQueuedReply(reason);
        if (!_aiSpeechActive && !NativeBubblePresenter.HasPendingReply)
            return;
        _aiSpeechActive = false;
        NativeBubblePresenter.ClearPending("cancelled", reason);
        try { DialogueManager.instance?.ForceEndDialogue(); }
        catch { /* The native bubble may already be gone. */ }
        ActionExecutor.Clear();
        NativeEventLog.Trace("ai.speech_cancelled", "mod", "player", "AI bubble interrupted by player",
            "reason=" + reason);
    }

    // Scheduling entry shared by the actual model reply path and the offline
    // harness: a completed generation waits for its playback window instead of
    // being written as spoken. A newer reply never silently drops an older
    // queued one; the older delivery record is closed as cancelled-before-display.
    [HideFromIl2Cpp]
    internal void QueueGeneratedReply(ApiResult result)
    {
        if (!NativeLinkageAllows(result)) return;
        result.EnforceMemoryOperationOutcome();
        if (_queuedReply != null && !ReferenceEquals(_queuedReply, result))
            CancelQueuedReply("superseded_by_newer_reply");
        _memory.EnsureReflectionDelivery(result.SourceUserEventId, result.DialogueRequest?.Id ?? result.RequestId);
        _queuedReply = result;
        NativeBubblePresenter.MarkQueuedForDisplay(result);
    }

    // Ends a generated reply waiting for its playback window. No display,
    // history, experience, extraction or Persona signal may follow.
    [HideFromIl2Cpp]
    internal void CancelQueuedReply(string reason, string outcome = "cancelled")
    {
        ApiResult? queued = _queuedReply;
        if (queued == null)
            return;
        _queuedReply = null;
        NativeBubblePresenter.CancelUndispatched(queued, outcome, reason);
        if (queued.Delivery?.IsTerminal == true)
            _memory.EndReflectionDelivery(queued.SourceUserEventId, queued.DialogueRequest?.Id ?? queued.RequestId,
                outcome, false);
        NativeEventLog.Trace("ai.queued_reply_cancelled", "mod", "system",
            "generated reply ended before display",
            "request_id=" + queued.RequestId
            + ";attempt_id=" + queued.Response.AttemptId
            + ";outcome=" + outcome + ";reason=" + reason
            + ";generated_chars=" + (queued.Delivery?.GeneratedChars ?? 0)
            + ";delivered_chars=" + queued.DeliveredText.Length);
    }

    [HideFromIl2Cpp]
    internal void OnAiBubbleShown(string text, ApiResult? reply, int chunkIndex, bool lastChunk)
    {
        _aiSpeechActive = true;
        _nativeConversation.Shown();
        if (reply?.NativeLinkage is { } native)
        {
            native.Shown = true;
            if (!native.EffectsApplied && NativeLinkageAllows(reply))
            {
                native.EffectsApplied = true;
                HandleDesktopArrangeIntent(reply);
                NativeMemoWriter.Apply(reply, _config);
                HandleReplyPaper(reply);
                ApplyAutonomousMemory(reply);
            }
        }
        else if (reply != null) CancelNativeLinkage("other_speech_shown");
        if (reply != null)
        {
            MemoryTurn? turn = _memory.AddHistory("assistant", text, reply.RequestId + ":" + chunkIndex,
                sourceEventId: reply.SourceUserEventId, isLocalFallback: reply.IsLocalFallback);
            if (turn != null)
            {
                _memory.RecordB1Delivery(reply, turn, reply.DeliveredText.Length);
                reply.DeliveredText += text;
                _sessionState.LastAssistantText = reply.DeliveredText;
                _sessionState.LastAssistantEventId = turn.EventId;
                _memory.AddInteractionMemory("莉莉丝说：" + text, "", "assistant_reply");
                _sessionState.AddSceneEvent(new LilithSceneEvent
                {
                    EventId = turn.EventId,
                    Kind = SceneEventKind.AssistantReply,
                    Actor = "lilith",
                    Details = text
                });
            }
            if (lastChunk)
                SettleAiDelivery(reply);
        }
        NativeEventLog.Trace("ai.speech_start", "mod", "lilith", "AI bubble speaking",
            "request_id=" + reply?.RequestId + ";attempt_id=" + reply?.Response.AttemptId
            + ";chunk=" + chunkIndex + ";last_chunk=" + lastChunk);
    }

    [HideFromIl2Cpp]
    internal void OnAiDeliveryEnded(ApiResult reply, string outcome)
    {
        // The presenter releases its own slot before notifying. A rejected stale
        // reply may settle itself, but must not release another active delivery.
        if (outcome != "completed" && !NativeBubblePresenter.HasPendingReply)
        {
            _aiSpeechActive = false;
            _lastAiSpeechEndAt = Time.unscaledTime;
        }
        FinishNativeLinkage(reply, reply.DeliveredText.Length > 0 ? "shown_" + outcome : "unshown_" + outcome);
        SettleAiDelivery(reply);
        // Only a fully delivered completed turn may become a past-thought source.
        if (reply.Delivery is { IsFullyDelivered: true })
            _memory.ThoughtLedger.Record(reply);
        ReplyDeliveryRecord? delivery = reply.Delivery;
        _memory.RecordB1Delivery(reply, outcome: outcome);
        if (delivery?.IsTerminal == true)
            _memory.EndReflectionDelivery(reply.SourceUserEventId, reply.DialogueRequest?.Id ?? reply.RequestId,
                delivery.Outcome, delivery.IsFullyDelivered,
                delivery.ShownChunks.Count == 0 ? "" : reply.RequestId + ":" + delivery.ShownChunks[^1].Index,
                delivery.ShownChunks.Count);
        NativeEventLog.Trace("ai.delivery_end", "mod", "lilith", "AI reply delivery ended",
            "request_id=" + reply.RequestId + ";attempt_id=" + reply.Response.AttemptId
            + ";outcome=" + outcome + ";delivered_chars=" + reply.DeliveredText.Length
            + ";generated_chars=" + (delivery?.GeneratedChars ?? reply.Text.Length)
            + ";shown_chunks=" + (delivery?.ShownChunks.Count ?? 0)
            + ";total_chunks=" + (delivery?.TotalChunks ?? 0)
            + ";abandoned_chunks=" + (delivery?.AbandonedChunks ?? 0)
            + ";abandoned_chars=" + (delivery?.AbandonedChars ?? 0)
            + ";reason=" + (delivery?.Reason ?? outcome));
    }

    [HideFromIl2Cpp]
    private void SettleAiDelivery(ApiResult reply)
    {
        if (!reply.TrySettleDeliveredPrefix())
            return;
        if (reply.IsMemoryOperation && reply.MemoryOperation == null)
            return;
        // The delivered prefix is the only text allowed to reach extraction and
        // the emotional signal; the unseen tail is never supplied as spoken.
        ReplyDeliveryRecord? delivery = reply.Delivery;
        NativeEventLog.Trace("ai.delivery_settled", "mod", "system",
            "controller settled the displayed delivery prefix",
            "request_id=" + reply.RequestId
            + ";outcome=" + (delivery?.Outcome ?? "partial")
            + ";reason=" + (delivery?.Reason ?? "")
            + ";generated_chars=" + (delivery?.GeneratedChars ?? reply.Text.Length)
            + ";delivered_chars=" + reply.DeliveredText.Length
            + ";shown_chunks=" + (delivery?.ShownChunks.Count ?? 0)
            + ";abandoned_chunks=" + (delivery?.AbandonedChunks ?? 0));
        if (!reply.IsMemoryOperation)
            TryScheduleAutonomousMemoryExtraction(reply);
        if (_config.PersonaRuntimeEnabled)
        {
            // The model's unseen tail cannot create an emotional text signal.
            // Action/emotion were attached to the first accepted bubble.
            PersonaRuntime.ObserveReply(new ApiResult
            {
                IsLocalFallback = reply.IsLocalFallback,
                SourceUserText = reply.SourceUserText,
                DeliveredText = reply.DeliveredText,
                Text = reply.DeliveredText,
                Action = reply.Action,
                Emotion = reply.Emotion
            }, _sessionState, DateTimeOffset.UtcNow);
        }
    }

    // Whether a completed generation is waiting for its playback window. The
    // delivery record already owns the generated text; the controller slot is
    // the queue. Shared with the offline harness so the state can be observed
    // without copying the controller's private fields.
    [HideFromIl2Cpp]
    internal bool HasQueuedDelivery => _queuedReply != null;

    // Input-independent cancellation transition shared by the real Update Esc
    // route and the offline harness. After OnApiResult/ApplyAiResult the request
    // flag is already false while the completed reply is still owned by the
    // controller queue, so a predicate built only from _requestPending can no
    // longer reach the cancel path. This entry decides from delivery ownership:
    // an ordinary request still being generated, a completed reply waiting for
    // its playback window, or a delivery that is currently being displayed.
    [HideFromIl2Cpp]
    internal bool TryCancelPendingDelivery(string source)
    {
        bool pendingOrdinary = _requestPending && (_activeDialogue != null
            || _nativeLinkage.ActiveAttempt is { Closed: false });
        bool waitingQueued = _queuedReply != null;
        bool displaying = _activeDialogue != null
            && (_aiSpeechActive || NativeBubblePresenter.HasPendingReply);
        if (!pendingOrdinary && !waitingQueued && !displaying && !_reviewHandoff.HasRunning)
            return false;
        bool requestedPending = _requestPending;
        bool hadQueued = _queuedReply != null;
        bool hadActiveOwner = _activeDialogue != null && !_activeDialogue.IsCancelled;
        CancelPendingDialogue();
        NativeEventLog.Trace("ai.cancel_route", "mod", "player",
            "pending delivery cancelled through the shared input-independent route",
            "source=" + source
            + ";request_pending=" + requestedPending
            + ";queued=" + hadQueued
            + ";active_owner=" + hadActiveOwner
            + ";owner_cancelled=" + (_activeDialogue?.IsCancelled == true)
            + ";queue_cleared=" + (_queuedReply == null));
        return true;
    }

    // UI-independent tail of Update's display step: once the playback window is
    // free, the queued reply is handed to the actual presenter. The gate that
    // decides when this may run stays in Update (native state); the harness
    // enters this same state transfer instead of duplicating the controller.
    [HideFromIl2Cpp]
    internal bool TryBeginQueuedReplyDisplay()
    {
        ApiResult? reply = _queuedReply;
        if (reply == null)
            return false;
        _queuedReply = null;
        NativeBubblePresenter.Show(reply);
        return true;
    }
}

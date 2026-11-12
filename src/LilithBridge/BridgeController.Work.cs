using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private readonly TemporaryExpressionSession _temporaryExpressions = new();
    private NativeWorkPlayback? _workPlayback;
    private bool _temporaryNativeEntry;
    [HideFromIl2Cpp]
    internal bool TemporaryOutputBlocksFinal => _workPlayback?.BlocksFinal == true;

    [HideFromIl2Cpp]
    private void BeginTemporaryWork(DialogueRequest owner, BridgeConfig config)
    {
        if (!config.ExperimentalTemporaryExpressions || !owner.NativeTools) return;
        var work = new DialogueWorkSession(owner.Id, owner.Token, () =>
            DataPaths.Root() == owner.ReviewDataRoot
            && _memory.ReviewSessionIdentity == owner.ReviewSessionIdentity,
            (config.ExperimentalPublicStreamMessages||config.ExperimentalPublicFinalStream)?64:DialogueRequest.MaxHttpAttempts) { SourceUserEventId = owner.UserEventId };
        owner.WorkPrototype = work; _workPlayback = new NativeWorkPlayback(work);
    }
    [HideFromIl2Cpp]
    private TemporaryWorkCarry CaptureTemporaryCarry(DialogueRequest? owner)
    {
        if (owner == null) return new("", () => true);
        var recovered = _memory.RecoverPublicExpressionCarry(_config,owner.ReviewCurrentText);
        var carry = _temporaryExpressions.Capture(recovered.ArchiveSources?.Select(s=>s.Metadata.PublicExpression!.SegmentId).ToArray());
        owner.PublicExpressionCarrySources = recovered.ArchiveSources??Array.Empty<GeneratedReplySnapshot>();
        owner.PublicExpressionCarryTracked = carry.Context.Length==0; // session-only final drafts/unknown fragments remain conservative
        string text = carry.Context+(recovered.Context.Length>0?"\n"+recovered.Context:"");
        owner.PublicExpressionCarryText = text;
        owner.WorkCarryPermission = ()=>carry.Permitted()&&recovered.Permitted();
        return new(text,owner.WorkCarryPermission,owner.PublicExpressionCarrySources);
    }
    [HideFromIl2Cpp]
    private void RetainAndStopTemporaryWork(string reason)
    {
        if (_workPlayback is not { } playback) return;
        playback.ObserveAppearance(Time.unscaledTime, Time.frameCount, atCancellation: true);
        playback.Stop(reason == "request_cancelled" ? "cancelled" : reason);
        _temporaryExpressions.Retain(playback.Work, playback.FinalDraft);
        _workPlayback = null;
    }
    [HideFromIl2Cpp]
    private void FinishTemporaryGeneration(ApiResult result)
    {
        if (result.DialogueRequest?.WorkPrototype is not { } work) return;
        if (result.Error.Length > 0) work.Finish("failed");
        else if (result.Text.Length == 0 && !NativeMemoReplySegments.AwaitingSettlement(result)) work.Finish();
        work.FlushArchive();
        // Success is sealed only after legacy recall/repair and the final reply
        // controls have reached QueueGeneratedReply. Do not stage final text twice.
    }
    [HideFromIl2Cpp]
    private void PrepareTemporaryFinal(ApiResult result)
    {
        TemporaryExpressionMemoryPolicy.PrepareFinal(result);
        if (ReferenceEquals(_workPlayback?.Work, result.DialogueRequest?.WorkPrototype)) _workPlayback?.RememberFinal(result);
    }
    private void TickTemporaryWork()
    {
        if (_workPlayback is not { } playback || _activeDialogue is not { } owner) return;
        if (!IsCurrentDialogue(owner))
        { if (owner.IsCancelled) CancelPendingDialogue(reason: "work_cancelled"); return; }
        bool allowed = !NativeStateInspector.IsSleeping && !_sessionState.PlayerOptionsShowing
            && !HasActiveNativeStoryForProactive() && !NativeBubblePresenter.HasPendingReply
            && (!ActionDirector.NativeBusy(_sessionState) || playback.Active != null);
        bool wasAppeared = playback.ActiveAppeared;
        bool attempted = playback.Tick(Time.unscaledTime, allowed, AiDeliveryEditing, PresentTemporaryBubble, Time.frameCount);
        if (!ReferenceEquals(_workPlayback, playback) || !IsCurrentDialogue(owner)) return;
        if (attempted && playback.Active != null)
        {
            _aiSpeechActive = true;
            // No AddHistory, interaction memory, persona update or extraction here.
        }
        if (!wasAppeared && playback.ActiveAppeared)
        {
            owner.WaitStatus.MarkFirstShown();
            NativeEventLog.Trace("work.bubble.appeared", "mod", "lilith", playback.Active!.Text,
                "whole_bubble=true;player_read=unknown");
        }
        if (playback.Work.Outcome is "display_unknown" or "display_unavailable" or "source_changed")
            CancelPendingDialogue(reason: playback.Work.Outcome);
    }
    [HideFromIl2Cpp]
    private WorkDisplayOutcome PresentTemporaryBubble(WorkBubble bubble)
    {
        _temporaryNativeEntry = true;
        try
        {
            var outcome = WorkDisplayOutcome.NotShown;
            bool Display() { outcome = NativeBubblePresenter.ShowTemporaryBubble(bubble); return true; }
            if (_activeDialogue?.TryUseSelection(Display) != true) return WorkDisplayOutcome.NotShown;
            return outcome;
        }
        finally { _temporaryNativeEntry = false; }
    }
    private bool FinishTemporaryBubble()
    {
        if (_workPlayback?.NativeEnded(Time.unscaledTime) != true) return false;
        _aiSpeechActive = false; _lastAiSpeechEndAt = Time.unscaledTime;
        _replyWindowUntil = _config.ReplyWindowSeconds <= 0 ? float.PositiveInfinity : Time.unscaledTime + _config.ReplyWindowSeconds;
        return true;
    }
}

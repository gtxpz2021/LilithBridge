using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace LilithBridge;

// Session-only identity for one native block's optional participation attempt.
// It is not an ordinary player request and never owns user input or memory.
internal sealed class NativeLinkageAttempt
{
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string BlockId { get; init; } = "";
    internal string[] Sources { get; init; } = Array.Empty<string>();
    internal CancellationTokenSource Cancellation { get; } = new();
    internal bool Shown { get; set; }
    internal bool EffectsApplied { get; set; }
    internal bool Closed { get; set; }
    internal bool Invalidated { get; set; }
    internal string Outcome { get; set; } = "pending";
}

// A capability for one already-authorized native attempt, not for proactive
// requests in general. Its controller identity and sources are rechecked on read.
internal sealed class NativeDialogueAuthorization
{
    private readonly NativeLinkageAttempt _attempt;
    private readonly Func<bool> _stillCurrent;
    internal IReadOnlyList<string> Sources { get; }
    internal NativeDialogueAuthorization(NativeLinkageAttempt attempt, Func<bool> stillCurrent)
    { _attempt = attempt; _stillCurrent = stillCurrent; Sources = attempt.Sources.ToArray(); }
    internal bool Permits(MemoryStore memory) => !_attempt.Closed && !_attempt.Invalidated
        && !_attempt.Cancellation.IsCancellationRequested && _stillCurrent()
        && memory.CurrentNativeSourcesAvailable(Sources);
}

internal sealed class NativeLinkageState
{
    internal bool BlockOpen { get; private set; }
    internal string BlockId { get; private set; } = "";
    internal List<string> SourceIds { get; } = new();
    private readonly HashSet<int> _nodes = new();
    private bool _eligible;
    internal bool HasPendingOpportunity => _eligible;
    internal float EndAt { get; private set; }
    internal DateTimeOffset EndedAtUtc { get; private set; }
    internal NativeLinkageAttempt? ActiveAttempt { get; private set; }
    internal string Reason => "本会话上一段实际游戏对话于 " + EndedAtUtc.ToString("O")
        + " 结束；相关原话按来源与顺序列在本次材料中。";
    internal void Begin()
    {
        BlockId = Guid.NewGuid().ToString("N"); BlockOpen = true;
        SourceIds.Clear(); _nodes.Clear(); _eligible = false; ActiveAttempt = null;
    }
    internal bool ObserveNode(int id) => BlockOpen && id > 0 && _nodes.Add(id);
    internal void AddSource(string id) { if (id.Length > 0 && !SourceIds.Contains(id)) SourceIds.Add(id); }
    internal void End(float now, DateTimeOffset utc, bool enabled)
    {
        if (!BlockOpen) return;
        BlockOpen = false; EndAt = now; EndedAtUtc = utc;
        _eligible = enabled && SourceIds.Count > 0;
    }
    internal bool Expired(float now) => !BlockOpen && now - EndAt > 180f
        && ActiveAttempt?.Shown != true;
    internal bool CanStart(float now) => _eligible && !BlockOpen && !Expired(now) && ActiveAttempt == null;
    internal NativeLinkageAttempt Start(float now)
    {
        if (!CanStart(now)) throw new InvalidOperationException("Native linkage opportunity unavailable");
        _eligible = false;
        return ActiveAttempt = new() { BlockId = BlockId, Sources = SourceIds.ToArray() };
    }
    internal void Cancel(string outcome)
    {
        _eligible = false;
        if (ActiveAttempt is { Closed: false } attempt)
        {
            attempt.Invalidated = true; attempt.Closed = true; attempt.Outcome = outcome;
            attempt.Cancellation.Cancel();
        }
    }
}

internal sealed partial class MemoryStore
{
    private readonly HashSet<string> _currentNativeSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentNativeSceneOnlySources = new(StringComparer.Ordinal);
    internal void BeginCurrentNativeBlock() { lock (_experienceLock) { _currentNativeSources.Clear(); _currentNativeSceneOnlySources.Clear(); } }
    internal void RegisterCurrentNativeSource(string id, bool sceneOnly = false)
    { lock (_experienceLock) { _currentNativeSources.Add(id); if (sceneOnly) _currentNativeSceneOnlySources.Add(id); } }
    internal string DescribeCurrentNativeSources(IReadOnlyList<MemoryTurn> history)
    {
        lock (_experienceLock)
        {
            var supplied = history.Where(t => _currentNativeSources.Contains(t.EventId)
                && IsNativeSupplyAllowed(t) && IsMentionAllowed(t) && !IsExperienceSuppressed(t.EventId)).ToList();
            if (supplied.Count == 0) return "";
            return "\n[当场原生对话来源] 下面保留游戏来源的台词，是本会话中实际发生的最近一段游戏对话。莉莉丝的台词是你在游戏里已经说过的话；玩家选项是对方当时实际选中的话。其他角色仍按原说话人理解。按原顺序与更新的交谈继续，不把未走的分支当共同经历。\n"
                + string.Join("\n", supplied.Select(t => "source=" + t.EventId + ";speaker=" + t.Role
                    + ";origin=" + t.Origin + ";at=" + t.Timestamp.ToString("O")));
        }
    }
    internal bool IsCurrentNativeSceneSource(string id)
    { lock (_experienceLock) return _currentNativeSceneOnlySources.Contains(id); }
    internal string DescribeCurrentNativeScenes(IReadOnlyList<LilithSceneEvent> events)
    {
        lock (_experienceLock)
        {
            var rows = events.Where(e => e.Kind == SceneEventKind.DialogueStart
                && _currentNativeSceneOnlySources.Contains(e.EventId) && IsRequestSceneAllowed(e))
                .GroupBy(e => e.EventId).Select(g => g.First()).ToList();
            if (rows.Count == 0) return "";
            return "\n[当场其他原作角色] 以下是本会话实际发生的游戏台词，按原说话人理解；不代表莉莉丝或眼前玩家说过，不把未走分支当共同经历。\n"
                + string.Join("\n", rows.Select(e => "source=" + e.EventId + ";origin=game;speaker="
                    + e.Actor + ";at=" + e.Timestamp.ToString("O") + ";text=" + e.Details));
        }
    }
    internal bool CurrentNativeSourcesAvailable(IReadOnlyList<string> ids)
    {
        lock (_experienceLock) return ids.Count > 0 && ids.All(id => _currentNativeSources.Contains(id)
            && !IsExperienceSuppressed(id)) && _history.Where(t => ids.Contains(t.EventId)).All(IsMentionAllowed);
    }
}

internal sealed partial class BridgeController
{
    private readonly NativeLinkageState _nativeLinkage = new();
    private NativeLinkageAttempt? NativeAttemptForRequest => _proactiveRequest
        && _nativeLinkage.ActiveAttempt is { Closed: false } current ? current : null;
    private NativeDialogueAuthorization? CaptureNativeDialogueAuthorization()
    {
        var attempt = NativeAttemptForRequest;
        if (attempt == null) return null;
        long generation = _conversationGeneration;
        var identity = new ApiResult { NativeLinkage = attempt, ConversationGeneration = generation };
        return NativeLinkageAllows(identity) ? new NativeDialogueAuthorization(attempt,
            () => NativeLinkageAllows(identity)) : null;
    }
    private void TickNativeLinkage()
    {
        if (!_config.ReactToNativeDialogue || !_config.ProactiveEnabled)
            CancelNativeLinkage("switch_off");
        else if (_nativeLinkage.Expired(Time.unscaledTime))
            CancelNativeLinkage("expired");
        else if (_nativeLinkage.SourceIds.Count > 0
            && !_memory.CurrentNativeSourcesAvailable(_nativeLinkage.SourceIds))
            CancelNativeLinkage("source_unavailable");
    }
    internal bool NativeLinkageAllows(ApiResult result)
    {
        if (!MatchesConversationGeneration(result)) return false;
        if(result.Error.Length==0 && result.DialogueRequest is { } reviewOwner && IsCurrentDialogue(reviewOwner))
        {
            string sourceReason = result.SharedSupply != null && !result.SharedSupply.TryUse(() => true) ? "shared_activity_changed"
                : !reviewOwner.ReviewSupplyPermitted() ? "review_source_changed"
                : !DeepSeekClient.MethodProtocolPermitted(reviewOwner) ? "method_source_changed" : "";
            if(sourceReason.Length>0)
            {
                reviewOwner.Cancel();
                // Review operations keep their own source snapshot and lifetime.
                if(ReferenceEquals(_queuedReply,result))CancelQueuedReply(sourceReason);
                else NativeBubblePresenter.CancelUndispatched(result,"cancelled",sourceReason);
                if(result.Delivery?.IsTerminal==true)
                    _memory.EndReflectionDelivery(reviewOwner.UserEventId,reviewOwner.Id,"cancelled",false);
                if(ReferenceEquals(reviewOwner,_activeDialogue))
                {_requestPending=false;_storyRecallInProgress=false;_memoryRecallInProgress=false;}
                return false;
            }
        }
        NativeLinkageAttempt? attempt = result.NativeLinkage;
        if (attempt == null) return true;
        return ReferenceEquals(attempt, _nativeLinkage.ActiveAttempt) && !attempt.Invalidated && !attempt.Closed
            && _config.ReactToNativeDialogue && _config.ProactiveEnabled
            && !_nativeLinkage.BlockOpen
            && (attempt.Shown || !_nativeLinkage.Expired(Time.unscaledTime))
            && _memory.CurrentNativeSourcesAvailable(attempt.Sources);
    }
    private void CancelNativeLinkage(string reason)
    {
        var attempt = _nativeLinkage.ActiveAttempt;
        bool owned = attempt is { Closed: false };
        bool pending = _nativeLinkage.HasPendingOpportunity;
        _nativeLinkage.Cancel(reason);
        if (!owned)
        {
            if (pending) NativeEventLog.Trace("native.linkage.closed", "mod", "system", reason,
                "block=" + _nativeLinkage.BlockId + ";attempt=not_started;shown=false");
            return;
        }
        if (ReferenceEquals(_queuedReply?.NativeLinkage, attempt)) CancelQueuedReply(reason);
        if (attempt!.Shown) CancelAiSpeech(reason);
        if (_proactiveRequest) { _requestPending = false; _proactiveRequest = false; }
        NativeEventLog.Trace("native.linkage.closed", "mod", "system", reason,
            "block=" + attempt.BlockId + ";attempt=" + attempt.Id + ";shown=" + attempt.Shown);
    }
    private void FinishNativeLinkage(ApiResult result, string outcome)
    {
        var attempt = result.NativeLinkage;
        if (attempt == null || attempt.Closed) return;
        attempt.Closed = true; attempt.Outcome = outcome;
        NativeEventLog.Trace("native.linkage.settled", "mod", "system", outcome,
            "block=" + attempt.BlockId + ";attempt=" + attempt.Id + ";shown=" + attempt.Shown);
    }
}

namespace LilithBridge;

// UI-independent part of native playback. Native completion/dismissal releases
// a slot; it never means that the player read or accepted the text.
internal sealed class NativeWorkPlayback
{
    internal DialogueWorkSession Work { get; }
    internal WorkBubble? Active { get; private set; }
    internal ApiResult? FinalDraft { get; private set; }
    private NativeAppearanceWait? _appearance;
    internal bool ActiveAppeared => Active != null && _appearance == null;
    internal void RememberFinal(ApiResult reply) => FinalDraft = reply;
    private double _nextAt;
    internal bool BlocksFinal => Active != null || Work.HasPendingPresentation;
    internal NativeWorkPlayback(DialogueWorkSession work) => Work = work;
    internal bool Tick(double now, bool allowed, bool editing, Func<WorkBubble, WorkDisplayOutcome> display, int frame = 0)
    {
        Work.PauseForEditing(editing);
        if (_appearance != null) return ObserveAppearance(now, frame);
        if (!allowed || editing || Active != null || now < _nextAt) return false;
        bool result = Work.TryPresentNext(b =>
        {
            Active = b; // Reserve before native entry; re-entrant stop cannot revive it.
            WorkDisplayOutcome receipt = display(b);
            if (Work.Outcome is not ("running" or "completed")) Active = null;
            else if (receipt == WorkDisplayOutcome.Accepted) _appearance = new(b.Text, now, frame);
            else if (receipt != WorkDisplayOutcome.Shown) Active = null;
            return receipt;
        });
        Work.FlushArchive();return result;
    }
    internal bool ObserveAppearance(double now, int frame, bool atCancellation = false)
    {
        if (_appearance == null || Active == null) return false;
        var outcome = _appearance.Poll(now, frame, atCancellation);
        if (outcome == WorkDisplayOutcome.Accepted) return false;
        Work.ResolveAppearance(Active.Id, outcome); _appearance = null;
        if (outcome != WorkDisplayOutcome.Shown) Active = null;
        Work.FlushArchive();
        return true;
    }
    internal bool NativeEnded(double now)
    {
        if (Active is not { } active) return false;
        if (_appearance != null) Work.ResolveAppearance(active.Id, WorkDisplayOutcome.Unknown);
        _appearance = null;
        Active = null;
        Work.ReleaseBubble(active.Id);
        _nextAt = now + BubbleChunks.PauseAfter(active.Text) / 1000d;
        Work.FlushArchive();
        return true;
    }
    internal void Stop(string reason) { Active = null; _appearance = null; Work.Stop(reason); Work.FlushArchive(); }
}

internal sealed record TemporaryWorkCarry(string Context, Func<bool> Permitted,GeneratedReplySnapshot[]? ArchiveSources=null);
internal sealed class TemporaryExpressionSession
{
    private readonly List<(DialogueWorkSession Work, ApiResult? Final)> _retained = new();
    internal string[] RetainedOperationIds => _retained.Select(x=>x.Work.OperationId).ToArray();
    internal void Retain(DialogueWorkSession work, ApiResult? final = null)
    {
        if (_retained.Any(x => ReferenceEquals(x.Work, work))) return;
        _retained.Add((work, final));
        while (_retained.Count > 4) { _retained[0].Work.Dispose(); _retained.RemoveAt(0); }
    }
    private static string Draft(ApiResult? final)
    {
        try
        {
            // This is retained data, not another operation by the old owner.
            // With no history-read snapshots there is no such source to revalidate;
            // the live-owner gate would otherwise reject every cancelled draft.
            // Keep it for any supplied history, plus all other source/method checks.
            bool hasHistorySources = final?.DialogueRequest?.ChatHistorySnapshots.Count > 0;
            if (final == null || final.Text.Length == 0 || final.Error.Length > 0 || final.Delivery?.IsFullyDelivered == true
                || final.SendPermission?.Allowed == false || !final.RequestSourcesPermitted(includeChatHistory: hasHistorySources)
                || final.DialogueRequest?.ReviewSupplyPermitted(includeChatHistory: hasHistorySources) == false
                || final.DialogueRequest is { } owner && !DeepSeekClient.MethodProtocolPermitted(owner)) return "";
            string draft = System.Text.Json.JsonSerializer.Serialize(new { kind = "undelivered_final_draft", text = final.Text,
                known_displayed_prefix = final.Delivery?.DeliveredText.Length>0?final.Delivery.DeliveredText:
                    final.FinalPublicDraft?.Selected==true&&MemoryStore.Clean(final.Text)==final.FinalPublicDraft.Body
                        ?string.Concat(final.FinalPublicDraft.Appearance().Select(e=>e.Text)):"", delivery_outcome = final.Delivery?.Outcome ?? "not_started",
                meaning = "草稿全文并非完整已说过的话；只有known_displayed_prefix确认程序展示，其他部分可能未显示或显示不确定。不是事实或新授权；可重新理解，不自动重放。" }, DialogueMaterialEnvelope.DataJson);
            var result = final.MemoWriteOutcome;
            if (result == null || result.Known && result.Eligible == 0 && result.Attempted == 0) return draft;
            // Reuse only a retained actual result under the draft's existing permissions.
            // This is information continuity, not cross-owner idempotency or permission to replay.
            return draft + "\n" + System.Text.Json.JsonSerializer.Serialize(new {
                kind = "prior_native_write_result", request_id = final.RequestId,
                draft_attempt_id = final.Response.AttemptId, source_user_event_id = final.SourceUserEventId,
                actual_result = result.ModelResult(),
                meaning = "这是该次请求已有的原生笔记或批注回执，不是新任务，也不表示草稿已经说出。confirmed已确认完成；unconfirmed未确认，仍可能已写入；unknown不能推断成未执行；unprocessed尚未处理，也没有排队。draft_attempt_id仅定位保留草稿，不是原生写入的执行ID。request_index只是该次规范化提案序号，不是原生存储ID。程序没有因这次交接重新执行或自动重试；不要把旧草稿或不确定回执当作重复写入的授权。"
            }, DialogueMaterialEnvelope.DataJson);
        }
        catch { return ""; }
    }
    private static string WorkText(DialogueWorkSession work,ApiResult? final,IReadOnlyList<string>? archivedSegments)
    {
        var excluded=(archivedSegments??Array.Empty<string>()).ToList();
        if(final?.FinalPublicDraft?.Selected==true&&MemoryStore.Clean(final.Text)==final.FinalPublicDraft.Body)
            excluded.Add(final.FinalPublicDraft.SegmentId);
        return excluded.Count>0?work.ContinuityForNextTurn(excluded):work.ContinuityForNextTurn();
    }
    internal TemporaryWorkCarry Capture(IReadOnlyList<string>? archivedSegments=null)
    {
        var captures = _retained.Select(x => (x.Work, x.Final,
                Text: WorkText(x.Work,x.Final,archivedSegments), Draft: Draft(x.Final)))
            .Where(x => x.Draft.Length > 0 || x.Text.Length>0&&x.Work.HasReceivedExpressions).ToArray();
        if (captures.Length == 0) return new("", () => true);
        string context = "[本会话临时表达·尚未进入长期历史] 程序显示不表示玩家读过；保留修正关系，未显示草稿不当作说过的话。\n"
            + string.Join("\n", captures.Select(x => x.Text + (x.Draft.Length == 0 ? "" : "\n" + x.Draft)));
        return new(context, () => captures.All(x => (x.Text.Length==0||WorkText(x.Work,x.Final,archivedSegments) == x.Text) && Draft(x.Final) == x.Draft));
    }
}

internal static class TemporaryExpressionMemoryPolicy
{
    internal static void PrepareFinal(ApiResult reply)
    {
        if (reply.DialogueRequest?.WorkPrototype is not { } work) return;
        work.Finish();
        work.FlushArchive();
        // A configured legacy failure/fallback may still yield a valid final
        // expression. Its handoff must not be lost because early work stopped.
        reply.WorkFinalReady = reply.Error.Length == 0;
    }
    // Explicit trial policy: intermediate expressions stay session-local. Only
    // final bubbles actually shown go through the existing raw-history writer.
    // This records an utterance, not truth, reading, or a permanent preference.
    internal static MemoryTurn? ArchiveFinalBubble(MemoryStore memory, ApiResult reply, string text, int chunkIndex,DateTimeOffset? observedAtUtc=null)
    {
        if (!reply.WorkFinalReady || reply.DialogueRequest?.WorkPrototype == null
            || reply.Error.Length > 0) return null;
        var display = reply.Delivery;
        if (display == null || display.RequestId != reply.RequestId || display.AttemptId != reply.Response.AttemptId
            || display.UserEventId != reply.SourceUserEventId
            || !display.ShownChunks.Any(chunk => chunk.Index == chunkIndex && chunk.Text == text)) return null;
        return memory.AddHistory("assistant", text, reply.RequestId + ":" + chunkIndex,
            sourceEventId: reply.SourceUserEventId, isLocalFallback: reply.IsLocalFallback,observedAtUtc:observedAtUtc);
    }
}

internal static class ScriptedDialogueInput
{
    // Shared by TickTestInput and the offline scripted replay. Manual input is
    // still allowed to interrupt; ordinary automatic input waits for all output.
    internal static bool CanAdvance(bool requestPending, bool recallPending, bool queuedFinal,
        bool regularBubble, bool temporaryOutput, bool trialEnabled = true, bool activeWork = false) =>
        !requestPending && !recallPending && (!(trialEnabled || activeWork) || !queuedFinal && !regularBubble && !temporaryOutput);
}

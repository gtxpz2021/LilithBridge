using System;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.Attributes;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private ApiResult? _memoResultContinuation;

    // Invoked only after the native writer, on the same display/main-thread
    // boundary. No draft or first-bubble effect has been delivered here.
    private bool TryStartMemoResultContinuation(ApiResult draft)
    {
        if (!NativeMemoResultInput.NeedsContinuation(draft)) return false;
        var owner = draft.DialogueRequest;
        if (owner == null || !owner.TryBeginMemoResultContinuation())
        { SetMemoResultFailure(draft, "owner_or_continuation_unavailable"); return false; }
        if (!_memory.PaperSourcesPermitted(draft.PaperSources))
        { CancelPendingDialogue(); return true; }
        if (owner.RemainingHttpAttempts <= 0)
        { SetMemoResultFailure(draft, "attempt_limit"); return false; }

        var input = new NativeMemoResultInput(_memory, draft.SourceUserText, draft.Text,
            draft.MemoEntries.Count > 0, draft.ScratchEntries.Count > 0, draft.MemoAnnotations.Count > 0,
            draft.MemoWriteOutcome!, draft.DeliveredText, draft.PaperSources);
        // Compose under the same permissions as the real request. Semantic
        // material can replace this base; the independent result input survives.
        var config = owner.RequestConfig;
        IReadOnlyList<MemoryTurn> history;
        string context;
        PaperSourceSnapshot freshSources;
        try
        {
            using var mention = _memory.BeginMentionRequest(input.Question, true);
            using var native = _memory.BeginNativeDialogueRequest(input.Question, true);
            using var material = MethodRuntimeEvidence.Begin(true);
            history = _memory.GetRequestHistory(config);
            context = ContextBuilder.Build(_memory, _sessionState, config, playerDirected: true, requestHistory: history);
            freshSources = _memory.CapturePaperSources(material.Sources
                .Concat(_memory.GetRequestHistorySelection(config, history)), _sessionState.RecentEvents, input.Question, true);
        }
        catch { SetMemoResultFailure(draft, "context_unavailable"); return false; }
        input = input with { ContextSources = freshSources };
        _queuedReply = null;
        _memoResultContinuation = draft;
        _requestPending = true;
        Task.Run(async () =>
        {
            ApiResult result;
            try
            {
                result = await _client.SendAsync(config, input.Question, context, null, owner.Token,
                    history, allowStoryRecall: false, thoughtBoundary: draft.ThoughtBoundary,
                    claimBoundary: draft.ClaimBoundary, requestPurpose: "dialogue:memo_result",
                    requestId: draft.RequestId, dialogueRequest: owner, historyOwner: _memory,
                    sharedActivitySupply: draft.SharedSupply, memoResultInput: input).ConfigureAwait(false);
                SubmittedMaterialBinding.Apply(result, owner, freshSources, null);
            }
            catch (Exception error) { result = new ApiResult { Error = error.GetType().Name }; }
            _mainThread.Enqueue(() => OnMemoResultContinuation(draft, result));
        });
        return true;
    }

    [HideFromIl2Cpp]
    private void OnMemoResultContinuation(ApiResult draft, ApiResult result)
    {
        if (!ReferenceEquals(_memoResultContinuation, draft)) return;
        if (!IsCurrentDialogue(draft.DialogueRequest) || !NativeLinkageAllows(draft))
        { CancelMemoResultContinuation("owner_changed"); return; }
        var sources = PaperSourceSnapshot.Combine(draft.PaperSources, result.PaperSources);
        if (!_memory.PaperSourcesPermitted(sources))
        { CancelPendingDialogue(); return; }
        _memoResultContinuation = null;
        _requestPending = false;
        bool accepted = NativeMemoResultInput.ExpressionOnly(result);
        if (!accepted)
        {
            SetMemoResultFailure(result, "expression_failed", draft.MemoWriteOutcome);
            result.ReasoningContent = "";
        }
        var expression = result;
        result = ApplyReplySafetyFallback(result);
        if (!ReferenceEquals(result, expression))
        {
            result.CopyResponseFrom(expression);
            result.SuccessfulModelRequests = expression.SuccessfulModelRequests;
        }
        result.SuccessfulModelRequests += draft.SuccessfulModelRequests;
        result.PromptTokens += draft.PromptTokens; result.CompletionTokens += draft.CompletionTokens;
        result.ReasoningTokens += draft.ReasoningTokens;
        result.PromptCacheHitTokens += draft.PromptCacheHitTokens; result.PromptCacheMissTokens += draft.PromptCacheMissTokens;
        result.CopyRequestFrom(draft);
        result.PaperSources = sources;
        result.MemoWriteOutcome = draft.MemoWriteOutcome;
        result.MemoResultFinal = true;
        result.MemoReplySettled = true;
        // Preserve the normal order: configured text fallback first, then the
        // original request's actual memory-operation outcome, before queueing.
        result.EnforceMemoryOperationOutcome();
        // Only a new, final attempt is queued. End the unshown draft without
        // ending the logical user's delivery or running any display callbacks.
        NativeBubblePresenter.CancelUndispatched(draft, "superseded", "memo_result_continued");
        _queuedReply = result;
        NativeBubblePresenter.MarkQueuedForDisplay(result);
    }

    private static void SetMemoResultFailure(ApiResult reply, string reason, NativeMemoWriteOutcome? outcome = null)
    {
        outcome ??= reply.MemoWriteOutcome;
        reply.Text = "这次回复没能正常完成。" + (outcome?.Summary ?? "笔记或批注的执行状态无法确认。");
        reply.Action = ""; reply.Animation = ""; reply.Voice = ""; reply.Emotion = "neutral"; reply.DesktopArrange = "";
        reply.Actions.Clear(); reply.MemoEntries.Clear(); reply.ScratchEntries.Clear(); reply.MemoAnnotations.Clear();
        reply.NoteSupplement.Clear(); reply.AutonomousMemories.Clear(); reply.AutonomousMemoryRemovals.Clear();
        reply.AutonomousMemoryPatches.Clear(); reply.MemoryRecalls.Clear(); reply.StoryRecalls.Clear();
        reply.ReflectionRequested = false; reply.B1 = null; reply.B1ChoiceProvided = false;
        reply.MemoConfirmation = ""; reply.MemoConfirmationProvided = false;
        reply.MemoReplySettled = true; reply.MemoResultFinal = true;
        reply.ExplicitEmptyText = false; reply.IsLocalFallback = true; reply.Error = "";
        NativeEventLog.Trace("memo.result.expression_failed", "mod", "system", reason);
    }

    private void CancelMemoResultContinuation(string reason, bool finishDelivery = true)
    {
        var draft = _memoResultContinuation;
        if (draft == null) return;
        _memoResultContinuation = null;
        NativeBubblePresenter.CancelUndispatched(draft, "cancelled", reason);
        if (finishDelivery)
            _memory.EndReflectionDelivery(draft.SourceUserEventId, draft.DialogueRequest?.Id ?? draft.RequestId, "cancelled", false);
        NativeEventLog.Trace("memo.result.cancelled", "mod", "system", reason,
            "attempted=" + draft.MemoWriteOutcome?.Attempted + ";succeeded=" + draft.MemoWriteOutcome?.Succeeded);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
namespace LilithBridge;
internal sealed partial class BridgeController
{
    private void SubmitRequest(string text, string extraContext = "",
        string userEventId = "", UserMemoryOperationResult? memoryOperation = null,
        bool reviewAuthorized = false)
    {
        if (_requestPending || _storyRecallInProgress)
        {
            if (userEventId.Length > 0) _memory.EndUnstartedReflectionDelivery(userEventId);
            NativeEventLog.Trace("meta.proactive.pending_blocked", "mod", "system",
                "proactive request skipped because another request is pending");
            return;
        }
        if (_proactiveRequest)
        {
            _activeDialogue?.Dispose();
            _activeDialogue = null;
        }
        _pendingProactiveReason = null;
        string proactiveRecallQuery = _pendingProactiveRecallQuery;
        _pendingProactiveRecallQuery = "";
        _requestPending = true;
        _currentUserText = text;
        PersonaDecisionHint decision = PersonaStateEngine.Evaluate(_sessionState, _config);
        PersonaStateEngine.ApplyMoment(_sessionState, decision);
        _sessionState.TickRelationshipEvolution();
        _sessionState.PendingIntent = _proactiveRequest ? "主动留意玩家" : "回应玩家";
        if (PrivacySafety.IsCrisis(text))
        {
            _sessionState.Mood = MoodKind.Caring;
            _sessionState.MoodReason = "player_crisis_support";
            extraContext += "[安全优先] 玩家可能正在经历强烈痛苦或危险。先认真接住真实情绪，不玩设定、不评判、不催促；如果需要，鼓励联系可信任的人或心理援助热线。";
        }
        if (!_proactiveRequest && text.Length > 0)
        {
            _sessionState.ConversationTopic = text.Length > 24 ? text[..24] + "…" : text;
            _sessionState.ConversationTopicEventId = userEventId;
        }
        // Ordinary turns search with what the player just said. A proactive turn
        // may continue that topic only while the conversation is still warm, or
        // when its own reason names an unfinished topic; after a longer gap it
        // must not inherit the finished topic. Recent history stays in the
        // request either way, so she can always continue the immediate exchange.
        (string recallQuery, string recallQuerySource) = RuntimeDirector.ChooseRecallQuery(
            _proactiveRequest,
            proactiveRecallQuery,
            _sessionState.LastUserText,
            _config.ProactiveWarmContinuationSeconds,
            NativeStateInspector.TimeSinceLastInteractionSeconds);
        BridgeConfig requestConfig = _config;
        DialogueRequest? dialogueRequest = !_proactiveRequest
            ? BeginDialogueRequest(requestConfig, userEventId, text, reviewAuthorized: reviewAuthorized) : null;
        BindPreparationObservation(dialogueRequest);
        if (memoryOperation != null)
            dialogueRequest?.BindMemoryOperation(userEventId, memoryOperation);
        long submissionGeneration=_conversationGeneration;
        if(!_proactiveRequest&&requestConfig.SemanticRecallEnabled&&dialogueRequest!=null&&userEventId.Length>0)
        {
            QueueSemanticDialogue(dialogueRequest,submissionGeneration,recallQuery,
                ()=>ContinueSubmittedRequest(text,extraContext,userEventId,requestConfig,dialogueRequest,recallQuery,recallQuerySource,submissionGeneration));
            return;
        }
        ContinueSubmittedRequest(text,extraContext,userEventId,requestConfig,dialogueRequest,recallQuery,recallQuerySource,submissionGeneration);
    }

    private void ContinueSubmittedRequest(string text,string extraContext,string userEventId,BridgeConfig requestConfig,
        DialogueRequest? dialogueRequest,string recallQuery,string recallQuerySource,long submissionGeneration)
    {
        using var preparationCompose=PreparationTiming.MeasureOn(dialogueRequest?.PreparationTrace,"request.compose");
        var semanticComposeWatch=System.Diagnostics.Stopwatch.StartNew();
        if(!IsCurrentDialogue(dialogueRequest)||submissionGeneration!=_conversationGeneration)return;
        var semanticTrace=dialogueRequest?.SemanticMaterial!=null?new SemanticRecallTrace():null;
        using var mentionScope = _memory.BeginMentionRequest(text, !_proactiveRequest);
        var nativeDialogueAuthorization = CaptureNativeDialogueAuthorization();
        using var nativeDialogueScope = _memory.BeginNativeDialogueRequest(text, !_proactiveRequest, _config,
            nativeDialogueAuthorization);
        IReadOnlyList<MemoryTurn>? recallUsed = null;
        // Screens are requested by the dialogue model within the same user turn.
        // No lexical or unsolicited proactive capture runs in this trial.
        VisionImage? vision = null;
        IReadOnlyList<MemoryTurn> requestHistory = _memory.GetRequestHistory(requestConfig);
        // Final permitted list for this request. The window may have been captured
        // before a withdrawal or a request-scope read rule changed; re-check it once
        // here so the context build, the supplied-material report and the client
        // payload all describe the same records.
        IReadOnlyList<MemoryTurn> finalRequestHistory =
            _memory.RevalidateRequestHistory(requestHistory);
        // Local buffer per Build call: one request may build more than once, and
        // the two chains must never be joined. Correlation stays here.
        var reflectionObservations = new List<ReflectionProjectionObservation>();
        using var b1Capture = B1Continuity.BeginCapture(dialogueRequest?.B1HostFootprint ?? new List<MemoryTurn>());
        using var methodRuntime = MethodRuntimeEvidence.Begin(requestConfig.SessionReadMethodsEnabled);
        string context = ContextBuilder.Build(
            _memory,
            _sessionState,
            requestConfig,
            list => recallUsed = list,
            environment: SemanticEnvironmentForTests,
            allowReflectionChatProjection: !_proactiveRequest,
            recallQueryOverride: recallQuery,
            imageAttached: vision is { DataUrl.Length: > 0 },
            onReflectionProjectionObservation: reflectionObservations.Add,
            playerDirected: !_proactiveRequest, requestHistory: finalRequestHistory,
            includeVisionStatus: dialogueRequest == null,
            semanticRecall: dialogueRequest?.SemanticMaterial?.Result, semanticTrace: semanticTrace);
        context += Environment.NewLine + _sessionState.VisualSupply.Describe(
            _memory.GetRequestHistorySelection(requestConfig, finalRequestHistory));
        try
        {
            ReflectionProjectionTrace.Write(
                userEventId.Length > 0 ? userEventId : "request-" + Guid.NewGuid().ToString("N"),
                reflectionObservations,
                context);
        }
        catch
        {
            // Observation is a bypass; it never changes what the chat receives.
        }
        if (_desktopArrangeProposePending
            && Time.unscaledTime - _desktopArrangeProposalAt
                <= _config.DesktopArrangeProposalExpirySeconds)
        {
            extraContext = "玩家正在回应莉莉丝刚才提出的桌面整理建议。如果玩家同意整理，就在自然回复里附带 <desktop_arrange:confirm>；如果拒绝，附加 <desktop_arrange:cancel>；如果话题已经换掉，什么标签都不要加。" + extraContext;
        }
        NativeEventLog.Trace("context.preview", "mod", "system",
            "context preview",
            context.Length > 2400 ? context[..2400] : context);
        if (!string.IsNullOrWhiteSpace(extraContext))
        {
            string contextLabel = _proactiveRequest
                ? ""
                : "[程序自动观察] ";
            context += Environment.NewLine + contextLabel + extraContext;
        }
        if(dialogueRequest!=null)
        {
            dialogueRequest.MethodRuntimeTracked=methodRuntime.Available&&string.IsNullOrWhiteSpace(extraContext);
            dialogueRequest.MethodRuntimeSources=methodRuntime.Sources;
        }
        // The automatic story-archive recall that used to be appended here now
        // travels inside ContextBuilder.Build's single material selection
        // (ContextMaterialSelection): it is one decision with the story block the
        // request already carries, so this old bypass would append a second copy.
        // The explicit, model-requested story recall below is a different path and
        // remains unchanged.
        NativeEventLog.Trace("deepseek.request", "mod", "system",
            "request started",
            (vision?.DataUrl.Length > 0 ? "vision=yes" : "vision=no")
                + ";proactive=" + _proactiveRequest
                + ";recall_query_source=" + recallQuerySource);
        ObservePlayerClaim(text, userEventId);
        ThoughtRecallBoundary thoughtBoundary = _memory.CaptureThoughtRecallBoundary(text,
            requestConfig.ReflectionEnabled && requestConfig.RuntimeContextCleanupEnabled,dialogueRequest?.SemanticMaterial?.Result);
        // Every recognized historical-claim question carries original source
        // candidates. Similarity selects material but never establishes support.
        ClaimRecallBoundary claimBoundary = _memory.CaptureClaimBoundary(text);
        long conversationGeneration = submissionGeneration;
        NativeLinkageAttempt? nativeAttempt = NativeAttemptForRequest;
        string requestPurpose = _proactiveRequest ? "proactive" : "dialogue";
        if (claimBoundary.Sources.Count > 0)
            recallUsed = (recallUsed ?? Array.Empty<MemoryTurn>()).Concat(claimBoundary.Sources)
                .GroupBy(t => t.EventId).Select(g => g.First()).ToList();
        if(dialogueRequest?.SemanticMaterial!=null)
            BindSemanticMaterial(dialogueRequest,conversationGeneration,recallQuery,requestConfig,extraContext,
                context,finalRequestHistory,semanticTrace!,recallUsed??Array.Empty<MemoryTurn>(),thoughtBoundary,claimBoundary);
        LastRequestComposeMs=semanticComposeWatch.Elapsed.TotalMilliseconds;
        ParticipationSnapshot? participationSnapshot = _proactiveRequest
            ? _sessionState.CaptureParticipationSnapshot() : null;
        SharedActivitySupply? sharedSupply = _proactiveRequest ? null
            : CaptureSharedActivitySupply(dialogueRequest?.Id ?? Guid.NewGuid().ToString("N"));
        Task.Run(async () =>
        {
            if (submissionGeneration != _conversationGeneration) return;
            var images = vision is { DataUrl.Length: > 0 }
                ? new List<VisionImage> { vision }
                : null;
            ApiResult result = await _client.SendAsync(
                requestConfig,
                text,
                context,
                images,
                nativeAttempt?.Cancellation.Token ?? System.Threading.CancellationToken.None,
                finalRequestHistory, thoughtBoundary: thoughtBoundary,
                claimBoundary: claimBoundary, requestPurpose: requestPurpose,
                dialogueRequest: dialogueRequest, historyOwner: _memory,
                participationSnapshot: participationSnapshot, requestId: sharedSupply?.RequestId,
                sharedActivitySupply: sharedSupply).ConfigureAwait(false);
            result.NativeLinkage = nativeAttempt;
            result.ConversationGeneration = conversationGeneration;
            _mainThread.Enqueue(() =>
            {
                if (!IsCurrentDialogue(dialogueRequest) || !NativeLinkageAllows(result)) return;
                if(result.Error.Length==0&&dialogueRequest?.SemanticMaterial is { } materialState)
                {
                    foreach(var sent in materialState.HttpAudit.Where(a=>a.Outcome.StartsWith("accepted",StringComparison.Ordinal)))
                        _memory.RecordRecall(recallQuery,_memory.ResolveSemanticAudit(sent.Sources),"main_chat");
                }
                else if (recallUsed != null && recallUsed.Count > 0 && result.Error.Length == 0)
                    for (int i=0; i<result.SuccessfulModelRequests; i++)
                        _memory.RecordRecall(recallQuery, recallUsed, "main_chat");
                OnApiResult(result);
            });
        });
    }

}

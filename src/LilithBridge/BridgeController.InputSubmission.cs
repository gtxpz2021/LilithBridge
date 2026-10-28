using System;
using UnityEngine;
using Il2CppInterop.Runtime.Attributes;
namespace LilithBridge;
internal sealed partial class BridgeController
{
    private readonly ReviewTurnAuthorization _reviewAuthorization = new();
    private readonly SubmitEnterGate _submitEnter = new();
    internal bool ReadEnterKey(bool down, bool focused)
        => _submitEnter.Read(down, focused, _listening, Time.unscaledTime);
    private bool ConsumeReviewAuthorization() => _reviewAuthorization.Consume();
    private bool CanOpenReplyWindow()
    {
        return _systemReady
            && !_listening
            && !NativeStateInspector.IsSleeping
            && (_requestPending || _storyRecallInProgress || _queuedReply != null || _config.ReplyWindowSeconds <= 0
                || (_replyWindowUntil > 0f && Time.unscaledTime <= _replyWindowUntil))
            && !_sessionState.PlayerOptionsShowing
            && (_aiSpeechActive
                || _sessionState.NativeDialogueActive
                || _sessionState.BusyOrAwaitingResponse
                || !ActionDirector.NativeBusy(_sessionState));
    }

    private void OnInputSubmitted(string text)
    {
        using var preparationObservation=BeginPreparationObservation();
        _submitEnter.Submitted(Time.unscaledTime);
        // The authorization belongs to the submitted input even when a local
        // memory command or an empty input ends the turn without a model call.
        bool reviewAuthorized = ConsumeReviewAuthorization();
        _listening = false;
        try
        {
            TransparentWindowNew.EndKeyboardInput();
        }
        catch
        {
            // Ignore.
        }
        SubmitPlayerText(text,reviewAuthorized);
    }
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    internal void SubmitPlayerText(string text,bool reviewAuthorized=false)
    {
        text=MemoryStore.Clean(text);
        if(text.Length==0)return;
        // Formal nonempty input, never opening/draft/IME, transfers ownership.
        CancelPendingDialogue(preserveReview:true);
        ValidPlayerSubmission();
        CancelAiSpeech("input_submit");
        _proactiveRequest=false;_pendingProactiveReason=null;
        _memory.CaptureProfile(text);
        MemoryTurn? userTurn;
        using(PreparationTiming.Measure("input.archive"))userTurn=_memory.AddHistory("user",text);
        PreparationTiming.Current?.Mark("input.archived");
        _reviewHandoff.Trim(_memory,_config);
        if(TryHandleMemoryCommand(userTurn,out var memoryOutcome))return;
        _sessionState.LastUserText=text;_sessionState.LastUserEventId=userTurn?.EventId??"";
        _sessionState.AddSceneEvent(new LilithSceneEvent{EventId=userTurn?.EventId??Guid.NewGuid().ToString("N"),Kind=SceneEventKind.UserInput,Actor="player",Details=text});
        _memory.AddInteractionMemory("玩家说："+text,"","user_input");
        NativeEventLog.Trace("chat.send","mod","player",text);
        SubmitRequest(text,userEventId:userTurn?.EventId??"",memoryOperation:memoryOutcome,reviewAuthorized:reviewAuthorized);
    }
    private void InjectPlayerText(string text)
    { SubmitPlayerText(text,ConsumeReviewAuthorization());_lastTestInputSentAt=Time.unscaledTime; }
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    internal void ValidateReviewHandoffBoundary()
    {
        _memory.SyncReadMethods(_config);
        _reviewHandoff.InvalidateScope(_memory,_config);
        if(_activeDialogue is {IsCancelled:false} owner && owner.ReviewDataRoot.Length>0
            && (owner.ReviewDataRoot!=DataPaths.Root() || owner.ReviewConfigRoot!=_config.DataDirectory
                || owner.ReviewSessionIdentity!=_memory.ReviewSessionIdentity))
        {CancelPendingDialogue();return;}
        if(_reviewHandoff.Current is not { } p)return;
        p.RevalidateInFlight(_memory);
        if(ReviewContextModes.Normalize(_config.ReviewContextMode)==ReviewContextModes.Off)
            _reviewHandoff.Stop("permission_revoked");
    }
}

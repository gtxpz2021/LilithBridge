using System;
using UnityEngine;
namespace LilithBridge;

// A tail clock only. Gameplay ownership remains in the original game and the
// existing controller request/presenter fields, passed in at each observation.
internal sealed class NativeConversationProtection
{
    private bool _tailEarned, _wasBusy;
    internal float Until { get; private set; } = float.NegativeInfinity;
    internal void Submitted() => _tailEarned = true;
    internal void Shown() => _tailEarned = true;
    internal bool Observe(float now, bool busy, int tailSeconds)
    {
        if (tailSeconds <= 0) Until = float.NegativeInfinity;
        if (_wasBusy && !busy && _tailEarned)
        { Until = Math.Max(Until, now + Math.Clamp(tailSeconds, 0, 600)); _tailEarned = false; }
        _wasBusy = busy;
        return busy || now < Until;
    }
    internal void ReleaseForNative() { _tailEarned = false; _wasBusy = false; Until = float.NegativeInfinity; }
}

internal sealed partial class BridgeController
{
    private readonly NativeConversationProtection _nativeConversation = new();
    private long _conversationGeneration = 1;
    private bool ConversationBusy => _listening || _historyWindow.IsOpen || _requestPending
        || _storyRecallInProgress || _memoryRecallInProgress || _queuedReply != null
        || _aiSpeechActive || NativeBubblePresenter.HasPendingReply;
    internal bool ProtectsNativeStarts => _nativeConversation.Observe(Time.unscaledTime,
        ConversationBusy, _config.NativeDialogueAfterAiCooldownSeconds);
    private void ObserveNativeConversation() => _ = ProtectsNativeStarts;
    private void ValidPlayerSubmission()
    {
        _conversationGeneration++;
        InterruptSharedActivity();
        _nativeConversation.Submitted();
        // Submission precedes SubmitRequest/BeginDialogueRequest in the same frame.
        _nativeConversation.Observe(Time.unscaledTime, true, _config.NativeDialogueAfterAiCooldownSeconds);
    }
    internal void BeforeExplicitNativeChoice()
    {
        _conversationGeneration++;
        InterruptSharedActivity();
        CancelNativeLinkage("explicit_native_choice");
        CancelPendingDialogue();
        CancelAiSpeech("explicit_native_choice");
        _requestPending = _storyRecallInProgress = _memoryRecallInProgress = false;
        _proactiveRequest = false;
        _pendingProactiveReason = null;
        ActionExecutor.Clear();
        _nativeConversation.ReleaseForNative();
    }
    private bool MatchesConversationGeneration(ApiResult result) => result.ConversationGeneration == 0
        || result.ConversationGeneration == _conversationGeneration;
}

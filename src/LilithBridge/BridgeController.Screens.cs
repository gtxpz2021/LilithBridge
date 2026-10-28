using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private DialogueRequest? _activeDialogue;
    private readonly ReviewHandoff _reviewHandoff = new();
    private PendingScreenCapture? _pendingScreenCapture;

    private sealed record PendingScreenCapture(DialogueRequest Owner, int EarliestFrame,
        float EarliestTime, TaskCompletionSource<VisionImage> Completion, CancellationToken Token);

    [HideFromIl2Cpp]
    internal DialogueRequest BeginDialogueRequest(BridgeConfig config, string userEventId,
        string? userText = null, string? modeOverride = null, bool reviewAuthorized = false)
    {
        bool requestPending = _requestPending;
        if (_activeDialogue != null && !_activeDialogue.IsCancelled) CancelPendingDialogue(preserveReview:true);
        _requestPending = requestPending;
        _activeDialogue?.Dispose();
        DialogueRequest? owner = null;
        owner = new DialogueRequest(config, userEventId, token => CaptureForDialogueAsync(owner!, token),
            userText, modeOverride, reviewAuthorized: reviewAuthorized);
        _reviewHandoff.Configuration=()=>_config;
        owner.ReviewHandoff=_reviewHandoff;
        owner.MethodOwner=_memory.SyncReadMethods(config);
        owner.ReviewStore=_memory;owner.ReviewCurrentText=userText??"";
        owner.ReviewDataRoot=DataPaths.Root();owner.ReviewConfigRoot=config.DataDirectory;owner.ReviewSessionIdentity=_memory.ReviewSessionIdentity;
        _activeDialogue = owner;
        _memory.BeginReflectionDelivery(userEventId, owner.Id);
        return owner;
    }

    [HideFromIl2Cpp]
    private async Task<VisionImage> CaptureForDialogueAsync(DialogueRequest owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<VisionImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        _mainThread.Enqueue(() =>
        {
            if (!IsCurrentDialogue(owner) || token.IsCancellationRequested)
            { completion.TrySetCanceled(token); return; }
            // Main-thread frames only. Input has been hidden before submission;
            // two further frames prevent capture of the pre-hide render.
            _pendingScreenCapture = new(owner, Time.frameCount + 2,
                Time.unscaledTime + 0.05f, completion, token);
        });
        return await completion.Task.ConfigureAwait(false);
    }

    [HideFromIl2Cpp]
    internal bool IsCurrentDialogue(DialogueRequest? request) => request == null
        || (ReferenceEquals(request, _activeDialogue) && !request.IsCancelled);

    [HideFromIl2Cpp]
    internal void TickScreenCapture()
    {
        var pending = _pendingScreenCapture;
        if (pending == null) return;
        if (pending.Token.IsCancellationRequested || !IsCurrentDialogue(pending.Owner))
        {
            _pendingScreenCapture = null;
            pending.Completion.TrySetCanceled(pending.Token);
            return;
        }
        if (Time.frameCount < pending.EarliestFrame || Time.unscaledTime < pending.EarliestTime) return;
        _pendingScreenCapture = null;
        if (_listening || !_config.VisionEnabled)
        {
            pending.Completion.TrySetResult(new VisionImage { CaptureOutcome = "input_open_or_vision_disabled" });
            return;
        }
        VisionImage frame = VisionCapture.Capture(_config);
        if (pending.Token.IsCancellationRequested || !IsCurrentDialogue(pending.Owner))
            pending.Completion.TrySetCanceled(pending.Token);
        else pending.Completion.TrySetResult(frame);
    }

    [HideFromIl2Cpp]
    internal void CancelPendingDialogue(bool preserveReview=false)
    {
        if(!preserveReview)_reviewHandoff.Stop();
        _activeDialogue?.PreparationTrace?.Finish("cancelled");
        // Nullable-owner proactive callbacks share this cancellation generation.
        _conversationGeneration++;
        // A generated reply waiting for its playback window belongs to the
        // cancelled request: close its delivery record before display so it
        // cannot arrive late or enter history.
        CancelNativeLinkage("request_cancelled");
        CancelQueuedReply("request_cancelled");
        _requestPending = false;
        _storyRecallInProgress = false;
        _memoryRecallInProgress = false;
        if (_activeDialogue == null) {CancelAiSpeech("request_cancelled");return;}
        var cancelled = _activeDialogue;
        cancelled.Cancel();
        _pendingScreenCapture?.Completion.TrySetCanceled(cancelled.Token);
        _pendingScreenCapture = null;
        CancelAiSpeech("request_cancelled");
        // Ownership is cancelled, callbacks reject this request, and both the
        // controller queue and presenter have now been synchronously cleared.
        _memory.EndReflectionDelivery(cancelled.UserEventId, cancelled.Id, "cancelled", false);
        NativeEventLog.Trace("dialogue.cancelled", "mod", "player", "ordinary dialogue cancelled",
            "request_id=" + cancelled.Id + ";http_attempts=" + cancelled.HttpAttempts);
    }

    [HideFromIl2Cpp]
    private void RecordVisualSupply(ApiResult result)
    {
        if (result.DialogueRequest is not { } request) return;
        _sessionState.VisualSupply.Record(request.Receipt());
        VisionImage? frame = request.Frame;
        NativeEventLog.Trace("vision.supply", "mod", "system", "request-bound visual supply",
            JsonSerializer.Serialize(new
            {
                request_id = request.Id, user_event_id = request.UserEventId,
                requested = request.ScreenRequested, outcome = request.Receipt().Outcome,
                capture_outcome = frame?.CaptureOutcome ?? "not_requested",
                captured_at = frame?.CapturedAt, x = frame?.X, y = frame?.Y,
                source_width = frame?.SourceWidth, source_height = frame?.SourceHeight,
                width = frame?.Width, height = frame?.Height, sha256 = frame?.Sha256 ?? ""
            }));
    }
}

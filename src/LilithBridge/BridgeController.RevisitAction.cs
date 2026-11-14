using System;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    // The MAIN continuation waits only for this short owner-thread admission,
    // never for the reflection model or its result application.
    private async Task<ThoughtRevisitReceipt> AdmitMainThoughtRevisit(DialogueRequest request,
        ThoughtRevisitChoice choice, string toolCallId, ReflectionReasonBasis? basis, Func<bool> sourcePermission)
    {
        var completion = new TaskCompletionSource<ThoughtRevisitReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = request.Token.Register(() => completion.TrySetResult(new("not_accepted", "owner_cancelled")));
        _mainThread.Enqueue(() => request.WithRevisitAdmission(() =>
        {
            try
            {
                if (completion.Task.IsCompleted || !IsCurrentDialogue(request)
                    || !ReferenceEquals(request.ReviewStore, _memory) || request.ReviewDataRoot != DataPaths.Root()
                    || request.ReviewConfigRoot != _config.DataDirectory || request.ReviewSessionIdentity != _memory.ReviewSessionIdentity
                    || !request.CanExecuteNativeTool || !sourcePermission())
                { completion.TrySetResult(new("not_accepted", "owner_or_source_changed")); return; }
                using var mention = _memory.BeginMentionRequest(request.ReviewCurrentText, true);
                using var native = _memory.BeginNativeDialogueRequest(request.ReviewCurrentText, true, _config);
                var invocation = _memory.MainRevisitInvocation(choice, request.Id, request.UserEventId, toolCallId, basis);
                bool accepted = TryBeginExplicitThoughtRevisit(invocation, out string reason, out string operationId);
                completion.TrySetResult(new(accepted ? "accepted" : reason == "busy" ? "busy" : "not_accepted", reason, operationId));
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("memory.revisit.admission_error", "mod", "system", error.GetType().Name);
                completion.TrySetResult(new("not_accepted", "admission_failed"));
            }
        }));
        return await completion.Task.ConfigureAwait(false);
    }
}

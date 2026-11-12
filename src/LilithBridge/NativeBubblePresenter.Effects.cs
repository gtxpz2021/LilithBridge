using System.Text.Json;

namespace LilithBridge;

// A dispatch receipt, never proof of audible sound, finished animation, a new
// utterance, or player reading. ActionExecutor retains its native safety/defer
// policy. The presenter must not retry after entering that executor.
internal sealed record PresenterEffectsReceipt(string Boundary,string Outcome,string Reason,
    string EffectiveAction,DateTimeOffset RecordedAtUtc);

internal sealed partial class ApiResult
{internal PresenterEffectsReceipt? PresenterEffects {get;set;}}

internal static partial class NativeBubblePresenter
{
    // The actual Show/NotifyShown flow runs in focused tests. Only the native
    // effects boundary is replaced, as with NativeEndpointForTests for bubbles.
    internal static Action<ApiResult,LilithActionType?>? EffectsEndpointForTests {get;set;}
    internal static Func<LilithActionType?,LilithActionType?>? NativeActionPreparationForTests {get;set;}

    private static bool SubmitReplyEffects(ApiResult reply,LilithActionType? action,string boundary)
    {
        if(reply.PresenterEffects is {} prior)return prior.Outcome!="withheld";
        string reason="selection_changed";bool entered=false,current=false;
        PresenterEffectsReceipt Receipt(string outcome,string why)=>new(boundary,outcome,why,action?.ToString()??"",DateTimeOffset.UtcNow);
        try
        {
            current=reply.TryUseSelection(()=>
            {
                if(!ReferenceEquals(_activeReply,reply)){reason="presenter_owner_changed";return false;}
                if(reply.DialogueRequest?.IsCancelled==true){reason="request_cancelled";return false;}
                // Reserve before native entry, including re-entrant cancellation
                // or another Show. A return/exception cannot cause a second call.
                reply.PresenterEffects=Receipt("dispatching","");entered=true;
                if(EffectsEndpointForTests is {} endpoint)endpoint(reply,action);
                else ActionExecutor.Execute(reply,action,alreadyHandledByNativeSay:false);
                reply.PresenterEffects=Receipt("submitted","executor_may_defer;not_native_completion_proof");
                return true;
            });
        }
        catch(Exception error)
        {
            reason=error.GetType().Name;
            if(entered)reply.PresenterEffects=Receipt("dispatch_uncertain","exception_after_executor_entry:"+reason);
        }
        if(!entered)reply.PresenterEffects=Receipt("withheld",reason);
        try{NativeEventLog.Trace("native.reply.effects","mod","system","reply effects dispatch boundary",
            JsonSerializer.Serialize(new{request_id=reply.RequestId,attempt_id=reply.Response.AttemptId,receipt=reply.PresenterEffects}));}catch{ }
        // Cancellation can end the text delivery while the effects endpoint is
        // entered. Amend its pending receipt without creating speech or replay.
        if(reply.Delivery?.IsTerminal==true)
            try{BridgeController.Instance?.RecordGeneratedPublicEnd(reply,reply.DeliveryOutcome);}catch{ }
        return entered||current;
    }
    private static void CompleteWithoutNewBubble(ApiResult reply)
    {
        bool submitted=SubmitReplyEffects(reply,_activeAction,"terminal_without_new_bubble");
        if(!ReferenceEquals(_activeReply,reply))return;
        if(!submitted){ClearPending("cancelled","terminal_effects_"+reply.PresenterEffects?.Reason);return;}
        EndDelivery("completed","streamed_public_answer_already_shown;effects_"+reply.PresenterEffects?.Outcome,0);
    }
}

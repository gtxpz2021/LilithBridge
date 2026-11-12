using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private bool TryBindRecallSupplement(ApiResult original,RecallSupplement supplement)
    {
        try
        {
            if(!supplement.IsPermitted())throw new SemanticMaterialException("recall_source_changed");
            if(original.DialogueRequest?.SemanticMaterial is { } material)
                material.AddRecallSupplement(supplement,original.DialogueRequest);
            return true;
        }
        catch(SemanticMaterialException)
        {
            NativeBubblePresenter.CancelUndispatched(original,"cancelled","recall_source_changed");
            var rejected=new ApiResult {Error="补充资料已撤回或变更，本轮未继续发送"};
            rejected.CopyRequestFrom(original);rejected.Response.Outcome="recall_source_changed";
            rejected.Response.Provider=original.Response.Provider;OnApiResult(rejected);return false;
        }
    }
    // Both recall branches share generation ownership and a live source guard.
    // The semantic owner already holds the validated addition, not a second base.
    private async Task<ApiResult> SendRecallFollowUpAsync(BridgeConfig config, string userText,
        string context, IReadOnlyList<MemoryTurn> history, ApiResult original,
        PaperSourceSnapshot localSources, NativeMemoTarget? localTarget, string purpose,
        RecallSupplement? supplement = null)
    {
        config=original.DialogueRequest?.RequestConfig??original.GenerationConfig??config;
        bool Permitted()=>original.RequestSourcesPermitted()&&(supplement?.IsPermitted()??_memory.RecallSupplementPermitted(localSources));
        ApiResult result = await _client.SendAsync(config, userText, context, null,
            CancellationToken.None, history, false, original.ThoughtBoundary,
            claimBoundary: original.ClaimBoundary, requestPurpose: purpose, requestId: original.RequestId,
            dialogueRequest: original.DialogueRequest, historyOwner: _memory,
            requestSourcePermission:Permitted).ConfigureAwait(false);
        result.CopyRequestFrom(original);
        if (original.DialogueRequest?.SemanticMaterial != null)
            SubmittedMaterialBinding.Apply(result, original.DialogueRequest, localSources, localTarget);
        else
        {
            // With no semantic material owner the client sent this follow-up's
            // expanded context, including its freshly selected memo target.
            result.PaperSources = localSources;
            result.MemoAnnotationTarget = localTarget;
        }
        return result;
    }
}

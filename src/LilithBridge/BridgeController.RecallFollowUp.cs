using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    // Both existing recall branches return through this exact transport/binding
    // tail. A semantic owner may replace the local expanded context before send.
    private async Task<ApiResult> SendRecallFollowUpAsync(BridgeConfig config, string userText,
        string context, IReadOnlyList<MemoryTurn> history, ApiResult original,
        PaperSourceSnapshot localSources, NativeMemoTarget? localTarget, string purpose)
    {
        ApiResult result = await _client.SendAsync(config, userText, context, null,
            CancellationToken.None, history, false, original.ThoughtBoundary,
            claimBoundary: original.ClaimBoundary, requestPurpose: purpose, requestId: original.RequestId,
            dialogueRequest: original.DialogueRequest, historyOwner: _memory).ConfigureAwait(false);
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

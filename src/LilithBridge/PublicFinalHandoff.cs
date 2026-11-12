namespace LilithBridge;

// Pure mapping/import boundary used by the actual presenter and focused tests.
// Rebase existing appearance evidence to the complete answer's coordinates;
// this is not a second native show or a claim that the player read anything.
internal static class PublicFinalHandoff
{
    internal static bool Prepare(ApiResult reply,string text,ReplyDeliveryRecord delivery)
    {
        var draft=reply.FinalPublicDraft;
        if(draft?.Selected!=true)return true;
        // Provider terminal reconciliation has already allowed changes to its
        // unsubmitted tail in PublicFinalDraft.Seal. At this later local/UI
        // boundary the complete projected body must still match that terminal.
        // Merely preserving the shown prefix does not authorize a local rewrite.
        var plan=draft.FinalPlan(text);
        if(plan==null)return draft.ReadyChunks==0;
        delivery.PrepareFinalDisplayPlan(text,plan);return true;
    }
    internal static bool Import(ApiResult reply,ReplyDeliveryRecord delivery)
    {
        var draft=reply.FinalPublicDraft;
        if(draft?.Selected!=true||draft.ReadyChunks==0)return true;
        if(reply.DialogueRequest?.WorkPrototype?.HasPendingPresentation==true)return false;
        var evidence=draft.Appearance();var plan=delivery.DisplayPlan;
        if(plan==null||!plan.IsValid||evidence.Length!=draft.ReadyChunks||evidence.Length>plan.Chunks.Count
            ||evidence.Where((e,i)=>e.SegmentId!=draft.SegmentId||e.Index!=i||e.Text!=plan.Chunks[i].Text).Any())return false;
        return delivery.ImportStreamAppearance(evidence);
    }
}

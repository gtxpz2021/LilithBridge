namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static void CaptureExpressionSourceFootprint(MemoryStore? store,DialogueRequest owner,
        IReadOnlyList<MemoryTurn>? history,ApiResult envelope,bool imageAttached=false)
    {
        if(owner.WorkPrototype==null&&owner.PublicExpressionCarrySources.Length==0)return;
        string error="source_store_unavailable";MethodSource[]? sources=null;
        var generated=owner.GeneratedReplyAllSeen.Concat(owner.PublicExpressionCarrySources).Concat(owner.WorkPrototype?.ArchiveSourcesForCapture()??Array.Empty<GeneratedReplySnapshot>())
            .GroupBy(s=>s.Key,StringComparer.Ordinal).Select(g=>g.Last()).ToArray();
        bool tracked=!imageAttached&&owner.MethodRuntimeTracked&&owner.PublicExpressionCarryTracked&&!owner.PublicExpressionExternalPermissionSupplied
            &&owner.InitialExperience==null&&envelope.SharedSupply==null&&owner.SemanticMaterial?.RecallSupplementsPermitted()!=false;
        if(imageAttached)error="image_source_not_persistently_trackable";
        // A source-only footprint is insufficient to transfer an in-memory
        // history/process grant. Preserve the body, but withhold recovery.
        if(owner.HasChatHistorySupply||owner.ReviewBudget!=null){tracked=false;error="ephemeral_review_or_history_grant_not_transferable";}
        if(owner.StorySupplyChecks.Count>(owner.HasGeneratedReplySupplyGuard?1:0)){tracked=false;error="external_story_permission_not_serializable";}
        if(generated.Any(g=>g.Metadata.PublicExpression?.Sources.Complete==false||g.Metadata.PublicExpressionFinalSources?.Sources.Complete==false))
        {tracked=false;error="prior_public_expression_closure_unavailable";}
        if(store!=null)
        {
            var basis=(history??Array.Empty<MemoryTurn>()).Concat(owner.B1HostFootprint).Concat(owner.MethodRuntimeSources).ToArray();
            string captureError;
            bool onlyGeneratedGuard=owner.HasGeneratedReplySupplyGuard&&owner.StorySupplyChecks.Count==1&&owner.ReviewCalls==0&&!owner.HasReviewSupply;
            sources=store.CaptureMethodSources(owner,out captureError,basis,tracked,onlyGeneratedGuard);
            if(sources!=null)error="";else if(error=="source_store_unavailable")error=captureError;
        }
        envelope.PublicExpressionFootprint=new(sources!=null,error,sources??Array.Empty<MethodSource>(),
            generated.Select(s=>new PublicExpressionGeneratedSource(s.Key,s.BodyHash,MemoryStore.PublicExpressionImmutableIdentity(s))).ToArray(),
            owner.HasReviewSupply,owner.ReviewBudget?.Deadline);
    }
    private static void ArchiveOfferedExpression(MemoryStore? store,DialogueRequest owner,ApiResult generation,string segment)
    {
        if(store==null||owner.WorkPrototype is not {} work)return;
        var expression=work.ArchiveSnapshot().SingleOrDefault(s=>s.Id==segment);
        if(expression==null)return;
        var saved=store.CapturePublicExpression(generation,expression,work.Outcome);
        if(saved.Succeeded){work.BindArchive(segment,store,saved.Key);work.FlushArchive();}
        else NativeEventLog.Trace("work.expression.archive_failed","mod","system","Public expression archive was not confirmed",
            "attempt="+generation.Response.AttemptId+";reason="+saved.Reason);
    }
}

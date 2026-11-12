using System.Threading;
namespace LilithBridge;
internal sealed partial class BridgeController
{
    // Snapshot/commit remain on the existing owner. Only immutable source copies
    // cross the await. No model call, queue or persistent state is added when off.
    private void QueueContextRelevance(DialogueRequest owner,long generation,string query,BridgeConfig config,Action continuation)
    {
        if(!config.ContextRelevanceEnabled){continuation();return;}
        // Do not turn the auxiliary model into the sole memory gate when MAIN
        // has no authorized way to search independently. Respect the existing
        // off/request mode rather than silently enabling a tool permission.
        if((!owner.ReviewToolEnabled||!owner.CanStartReviewRead))
        {
            owner.SemanticMaterial??=new(owner.Id,generation,query,_memory.SemanticDataRoot);
            owner.SemanticMaterial.Result=(owner.SemanticMaterial.Result??new(owner.Id,MemoryStore.SemanticTextHash(query),
                SemanticRecallWorker.ModelRevision,"none",Array.Empty<SemanticRecallCandidate>(),"semantic_not_requested",false,
                _memory.SemanticDataRoot,_memory.ReviewSessionIdentity,Generation:generation)) with {RelevanceBypassed=true};
            NativeEventLog.Trace("context.relevance","mod","system","legacy supply retained","bypass:independent_read_not_enabled");
            continuation();return;
        }
        owner.WaitStatus.Prepare(); // Idempotent: retain the semantic-stage wait origin.
        ContextRelevanceSnapshot snapshot;
        try {snapshot=_memory.CaptureContextRelevance(owner.Id,generation,query,config,owner.SemanticMaterial?.Result);}
        catch(Exception error)
        {
            snapshot=new(){Owner=owner.Id,Generation=generation,Query=query,Root=_memory.SemanticDataRoot,Session=_memory.ReviewSessionIdentity,Status="snapshot_unavailable:"+error.GetType().Name};
        }
        var semantic=owner.SemanticMaterial?.Result??new(owner.Id,MemoryStore.SemanticTextHash(query),SemanticRecallWorker.ModelRevision,"relevance-local",Array.Empty<SemanticRecallCandidate>(),
            DataRoot:_memory.SemanticDataRoot,SessionId:_memory.ReviewSessionIdentity,Generation:generation);
        owner.SemanticMaterial??=new(owner.Id,generation,query,_memory.SemanticDataRoot);
        Task.Run(async()=>
        {
            ContextRelevancePlan plan;
            try {plan=await _client.RunContextRelevanceAsync(config,snapshot,()=>!owner.IsCancelled&&config.SendPermission?.Allowed!=false
                    &&_memory.ContextRelevanceSourcesCurrent(snapshot,config),owner.Token).ConfigureAwait(false);}
            catch(OperationCanceledException){return;}
            catch(Exception error){plan=new(snapshot,"judge_unavailable:"+error.GetType().Name);}
            _mainThread.Enqueue(()=>
            {
                if(_semanticDestroyed||!IsCurrentDialogue(owner)||generation!=_conversationGeneration||owner.IsCancelled)return;
                if(plan.Status=="accepted"&&!_memory.ContextRelevanceSourcesCurrent(snapshot,config))plan=new(snapshot,"source_changed_before_compose");
                owner.SemanticMaterial!.Result=(!owner.ReviewToolEnabled||!owner.CanStartReviewRead)?semantic with {RelevanceBypassed=true}
                    :_memory.ApplyContextRelevance(semantic,plan);
                NativeEventLog.Trace("context.relevance","mod","system","request-local material selection",
                    "status="+plan.Status+";presented="+snapshot.Candidates.Length+";selected="+plan.Choices.Count+";capacity_deferred="+snapshot.CapacityDeferred);
                continuation();
            });
        });
    }
}

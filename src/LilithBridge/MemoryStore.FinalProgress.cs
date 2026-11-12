using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private static string PublicFinalGenerationId(string session,string request,string attempt,string user,PublicStreamItemIdentity item)
        =>SemanticTextHash(JsonSerializer.Serialize(new {session,request,attempt,user,final_public_item=item}));
    internal string PublicFinalGenerationId(ApiResult generation,PublicStreamItemIdentity item)
        =>PublicFinalGenerationId(_sessionId,generation.RequestId,generation.Response.AttemptId,generation.SourceUserEventId,item);
    private static string PublicFinalCheckpointKey(PublicFinalCheckpoint progress)
        =>"generated/"+SemanticTextHash(JsonSerializer.Serialize(new {public_final_progress=progress.GenerationId,revision=progress.Revision}));
    private bool HasPublicFinalCanonical(GeneratedReplySnapshot progress)
    {
        if(progress.Metadata.PublicExpression?.FinalProgress is not {} p)return false;
        try
        {
            var canonical=LoadGenerated(GeneratedReplyKey(progress.SessionId,progress.RequestId,progress.AttemptId,progress.SourceUserId));
            return canonical?.Metadata.FinalPublicCanonical?.GenerationId==p.GenerationId&&QualifyGeneratedReply(canonical).Permitted;
        }
        catch{return false;}
    }
    private bool ValidatePublicFinalCanonical(GeneratedReplyMetadata metadata,string body,out GeneratedReplySnapshot? related)
    {
        related=null;
        if(metadata.FinalPublicCanonical is not {} link)return true;
        if(metadata.PublicExpression!=null||metadata.PublicExpressionFinalSources==null||!ValidGeneratedKey(link.CheckpointKey)||link.GenerationId.Length!=64)return false;
        // Inspect the target type before recursive load: malformed local metadata
        // cannot create a cycle of canonical links.
        var target=JsonSerializer.Deserialize<GeneratedReplyMetadata>(File.ReadAllText(GeneratedPath(link.CheckpointKey,".json"),GeneratedUtf8));
        if(target?.PublicExpression?.FinalProgress==null||target.FinalPublicCanonical!=null)return false;
        var prior=LoadGenerated(link.CheckpointKey);var p=prior?.Metadata.PublicExpression?.FinalProgress;
        bool valid=prior!=null&&p!=null&&p.GenerationId==link.GenerationId&&p.State=="selected_final_closed"&&prior.Body==body
            &&prior.SessionId==metadata.SessionId&&prior.RequestId==metadata.RequestId&&prior.AttemptId==metadata.AttemptId
            &&prior.SourceUserId==metadata.SourceUserEventId
            &&metadata.PublicExpressionFinalSources.Sources.GeneratedSources.Any(s=>s.Key==prior.Key&&s.BodyHash==prior.BodyHash&&s.ImmutableIdentity==PublicExpressionImmutableIdentity(prior));
        if(valid)related=prior;return valid;
    }
}

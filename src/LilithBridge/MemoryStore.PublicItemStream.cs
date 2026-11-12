using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private static string PublicStreamKey(string session,string request,string attempt,string user,PublicStreamItemIdentity item)
        =>"generated/"+SemanticTextHash(JsonSerializer.Serialize(new {session,request,attempt,user,stream_item=item}));
    internal static bool PublicStreamShape(PublicStreamItemIdentity item)=>item!=null&&item.ResponseId.Length is >0 and <=256
        &&item.ItemId.Length is >0 and <=256&&item.OutputIndex>=0&&item.ContentIndex>=-1&&item.Phase.Length<=64
        &&(item.WireTextHash==null||item.WireTextHash.Length==64);
    internal void RecordPublicStreamTerminal(string key,ApiResult generation)
    {
        if(DataPaths.Root()!=_root)return;
        lock(_experienceLock)
        lock(_generatedRepliesLock)
        {
            try
            {
                var saved=LoadGenerated(key);var m=saved?.Metadata;
                if(m?.PublicExpression?.StreamItem==null||m.RequestId!=generation.RequestId||m.AttemptId!=generation.Response.AttemptId
                    ||m.SourceUserEventId!=generation.SourceUserEventId||m.PublicExpression.WriterSessionId!=_sessionId)return;
                m.NetworkBodyComplete=generation.Response.BodyComplete;m.NetworkOutcome=generation.Response.Outcome;
                m.NetworkTerminalEvent=generation.Response.TerminalEvent;m.NetworkFinishReason=generation.Response.FinishReason;
                m.NetworkIncompleteReason=generation.Response.IncompleteReason;m.Revision++;
                m.PublicResponseItem=generation.PublicResponseItem;m.PublicResponseItems=generation.PublicResponseItems;SaveGeneratedMetadata(m);
            }
            catch(Exception error){GeneratedFailure("stream_terminal",key,error);}
        }
    }
}

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static OpenAIPublicItemStream? BeginPublicItemStream(BridgeConfig config,string kind,ApiResult generation)
    {
        var owner=generation.DialogueRequest;var work=owner?.WorkPrototype;var store=owner?.ReviewStore;
        if((!config.ExperimentalPublicStreamMessages&&!config.ExperimentalPublicFinalStream)||!config.ExperimentalTemporaryExpressions||!config.IsOpenAI||kind!="main"
            ||owner==null||work==null||store==null
            ||generation.SharedSupply!=null||generation.SharedDispatch!=null)return null;
        var observer=new OpenAIPublicItemStream(item=>
        {
            if(owner.IsCancelled||!WorkSourcesPermitted(generation,owner)||!generation.SelectionCurrent())return false;
            string text=item.Text;
            if(item.TerminalValidated)
            {
                if(text.Length>49152)return false;
                if(text.TrimStart().StartsWith('{'))
                {
                    try{using var json=System.Text.Json.JsonDocument.Parse(StripJsonFences(text));if(!json.RootElement.TryGetProperty("text",out var field)||field.ValueKind!=System.Text.Json.JsonValueKind.String)return false;}
                    catch(System.Text.Json.JsonException){return false;}
                }
                text=ParseReply(text,owner.ReviewCurrentText).Text;
            }
            if(!TryParsePublicExpression(text,owner.ReviewCurrentText,out var parsed)||parsed!.Text!=text)return false;
            work.SourceUserEventId=owner.UserEventId;
            bool offered=false;string key="";
            work.HoldArchivePublication(generation.Response.AttemptId);
            try
            {
                // Do not enter a store selection boundary while taking Work:
                // UI permission/display callbacks take Work -> source/store.
                // Publication is held; actual native display still uses the
                // existing owner.TryUseSelection boundary on the UI thread.
                if(!WorkSourcesPermitted(generation,owner)||!generation.SelectionCurrent()
                    ||!work.Offer(text,generation.Response.AttemptId,false,
                        CaptureWorkSourcesPermission(generation,owner),item.Identity))return false;
                var expression=work.ArchiveSnapshot().Last();
                if(!generation.SelectionCurrent()){work.WithholdUnpublished(expression.Id);return false;}
                var saved=store.CapturePublicExpression(generation,expression,work.Outcome);
                if(!saved.Succeeded){work.WithholdUnpublished(expression.Id);return false;}
                key=saved.Key;work.BindArchive(expression.Id,store,key);work.FlushArchive();
                offered=true;
                generation.PublicStreamArchiveKeys.Add(key);
                var captured=store.ReadPublicExpressionForCapture(key);
                if(captured!=null&&generation.PublicExpressionFootprint is {} footprint)
                    generation.PublicExpressionFootprint=footprint with {GeneratedSources=footprint.GeneratedSources.Append(
                        new(key,captured.BodyHash,MemoryStore.PublicExpressionImmutableIdentity(captured))).Distinct().ToArray()};
                return true;
            }
            finally
            {
                work.PublishAfterArchive(generation.Response.AttemptId);
                if(!offered)work.FlushArchive();
            }
        }){AllowEarly=config.ExperimentalPublicStreamMessages&&owner.HttpAttempts==1&&work.ArchiveSnapshot().Length==0};
        if(config.ExperimentalPublicFinalStream&&owner.HttpAttempts==1)
        {
            var draft=new PublicFinalDraft(generation,owner,work,store,
                CaptureWorkSourcesPermission(generation,owner),
                wire=>ParseReply(wire,owner.ReviewCurrentText).Text)
                {RequireFirstPublicItem=!config.ExperimentalPublicStreamMessages};
            observer.FinalDraft=draft;generation.FinalPublicDraft=draft;owner.FinalPublicDraft=draft;
        }
        return observer;
    }
    private static void FinishPublicItemStream(ApiResult generation)
    {
        if(generation.PublicStreamArchiveKeys.Count==0||generation.DialogueRequest is not {} owner||owner.ReviewStore is not {} store)return;
        if(owner.WorkPrototype is {} work)
        {
            _=work.Outcome; // Poll cancellation before writing appearance state.
            if(generation.Error.Length>0)work.Finish(owner.IsCancelled?"cancelled":"failed");
            work.FlushArchive();
        }
        foreach(string key in generation.PublicStreamArchiveKeys)store.RecordPublicStreamTerminal(key,generation);
    }
}

internal sealed partial class ApiResult
{
    internal List<string> PublicStreamArchiveKeys {get;}=new();
    internal PublicStreamItemIdentity? PublicResponseItem {get;set;}
    internal PublicItemReceipt[]? PublicResponseItems {get;set;}
}

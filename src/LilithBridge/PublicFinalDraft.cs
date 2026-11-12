using System.Text;
using System.Text.Json;

namespace LilithBridge;

// One public output_text generation, with bounded immutable progress versions.
// No provider reasoning, tool arguments, or incomplete structured reply enters
// this object. It is not a new prompt, model request, fact, or authorization.
internal sealed class PublicFinalDraft
{
    private readonly ApiResult _generation;
    private readonly DialogueRequest _owner;
    private readonly DialogueWorkSession _work;
    private readonly MemoryStore _store;
    private readonly PublicExpressionSourceFootprint? _sources;
    private readonly Func<bool> _permission;
    private readonly Func<string,string> _project;
    private readonly StringBuilder _raw=new();
    private readonly List<string> _ready=new();
    private string _response="",_item="",_segment="",_body="",_reason="";
    private int _output=-1,_sequence=-1,_through,_revision;
    private bool _part,_blocked,_syntaxBlocked,_sealed,_selected;
    internal string LatestKey {get;private set;}="";
    internal string GenerationId {get;private set;}="";
    internal PublicStreamItemIdentity? Identity {get;private set;}
    internal string SegmentId=>_segment;
    internal string Body=>_body;
    internal int ReadyChunks=>_ready.Count;
    internal bool Selected=>_selected;
    internal bool RequireFirstPublicItem {get;set;}
    internal PublicFinalDraft(ApiResult generation,DialogueRequest owner,DialogueWorkSession work,MemoryStore store,
        Func<bool> permission,Func<string,string> project)
    {_generation=generation;_owner=owner;_work=work;_store=store;_sources=generation.PublicExpressionFootprint;_permission=permission;_project=project;}
    private static string S(JsonElement e,string k)=>e.TryGetProperty(k,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??"":"";
    private static int N(JsonElement e,string k)=>e.TryGetProperty(k,out var p)&&p.TryGetInt32(out var n)?n:-1;
    private bool Matches(JsonElement e)=>S(e,"item_id")==_item&&N(e,"output_index")==_output&&N(e,"content_index")==0;
    private bool Allowed(){try{return _permission();}catch{return false;}}
    internal void Observe(JsonElement e)
    {
        if(_blocked||_sealed)return;
        int sequence=N(e,"sequence_number");
        if(sequence<0||sequence<=_sequence){_blocked=true;_reason="sequence_not_monotonic";return;}_sequence=sequence;
        string type=S(e,"type");
        if(type=="response.created")
        {if(e.TryGetProperty("response",out var r))_response=S(r,"id");return;}
        if(type=="response.output_item.added")
        {
            if(_item.Length>0||!e.TryGetProperty("item",out var item)||S(item,"type")!="message"||S(item,"role")!="assistant")return;
            // With early commentary disabled, do not overtake an earlier public
            // message. Preserve all items for the normal terminal handoff.
            if(S(item,"phase")!="final_answer")
            {
                if(RequireFirstPublicItem){_blocked=true;_reason="earlier_public_item";}
                return;
            }
            _item=S(item,"id");_output=N(e,"output_index");Identity=new(_response,_item,_output,0,"final_answer");
            if(!MemoryStore.PublicStreamShape(Identity)){_blocked=true;return;}
            GenerationId=_store.PublicFinalGenerationId(_generation,Identity);return;
        }
        if(_item.Length==0)return;
        if(type=="response.content_part.added"&&Matches(e))
        {
            if(_part||!e.TryGetProperty("part",out var p)||S(p,"type")!="output_text"||S(p,"text").Length>0){_blocked=true;return;}
            _part=true;return;
        }
        if(type!="response.output_text.delta"||!Matches(e))return;
        if(!_part||!e.TryGetProperty("delta",out var d)||d.ValueKind!=JsonValueKind.String){_blocked=true;return;}
        _raw.Append(d.GetString());
        if(_raw.Length>131072){_blocked=true;_reason="public_prefix_limit";return;}
        string raw=_raw.ToString(),start=raw.TrimStart();
        if(start.Length==0)return;
        if(start[0] is '{' or '[' or '<' or '`'){_syntaxBlocked=true;return;}
        int cut=raw.IndexOfAny(new[]{'<','`'});
        string body=MemoryStore.Clean(cut<0?raw:raw[..cut]);
        if(!body.StartsWith(_body,StringComparison.Ordinal)){_blocked=true;_reason="projection_changed";return;}
        _body=body;
        if(_segment.Length==0&&!Checkpoint(body,Array.Empty<string>(),0,"receiving"))
        {_blocked=true;_reason="initial_checkpoint_unavailable";return;}
        if(_syntaxBlocked||_revision>=24)return;
        var units=StableUnits(body,_through);if(units.Through<=_through||_ready.Count+units.Chunks.Length>48)return;
        if(!Checkpoint(body,_ready.Concat(units.Chunks).ToArray(),units.Through,"receiving"))
        {_blocked=true;_reason="checkpoint_unavailable";}
    }
    // Close only complete sentence units outside quotes. Require one following
    // public character, so a normal terminal handoff retains a final tail.
    internal static (string[] Chunks,int Through) StableUnits(string body,int from)
    {
        var chunks=new List<string>();var quotes=new Stack<char>();int begin=from,through=from;
        const string opens="“‘「『\"",closes="”’」』\"";
        for(int i=from;i<body.Length;i++)
        {
            char c=body[i];
            if(quotes.Count>0&&c==quotes.Peek())quotes.Pop();
            else {int q=opens.IndexOf(c);if(q>=0)quotes.Push(closes[q]);}
            if(quotes.Count>0)continue;
            // A bare ASCII period can be an abbreviation or decimal. Wait for
            // a paragraph boundary instead of exposing an unfinished English unit.
            bool end="。！？!?；;…".Contains(c)||c=='\n'&&i>0&&body[i-1]=='\n';
            // A quote closer may itself close a sentence that ended inside it.
            if(!end&&closes.Contains(c)&&i>0)end="。！？!?；;…".Contains(body[i-1]);
            if(!end)continue;
            int next=i+1;while(next<body.Length&&(char.IsWhiteSpace(body[next])||"。！？!?；;…”.’」』\"".Contains(body[next])))next++;
            if(next>=body.Length)continue;
            int last=i+1;while(last<body.Length&&"。！？!?；;…”.’」』\"".Contains(body[last]))last++;
            string unit=body[begin..last];
            if(!unit.Any(ch=>char.IsLetterOrDigit(ch)||char.IsSymbol(ch)||char.IsSurrogate(ch)))continue;
            // The legacy bubble splitter has one quote level. A closed quoted
            // unit stays whole here, including nested quotations.
            if(unit.Any(ch=>opens.Contains(ch)))chunks.Add(unit.Trim());
            else chunks.AddRange(BubbleChunks.Split(unit));
            begin=through=last;i=last-1;
        }
        return(chunks.ToArray(),through);
    }
    private bool Checkpoint(string body,IReadOnlyList<string> ready,int through,string state,bool stopped=false)
    {
        _work.HoldArchivePublication(_generation.Response.AttemptId);
        try
        {
            return CheckpointHeld(body,ready,through,state,stopped);
        }
        finally{_work.PublishAfterArchive(_generation.Response.AttemptId);}
    }
    // Caller holds publication before it samples the committed prefix. Taking
    // that snapshot first lets UI submission advance Next during permission
    // checks, so SetFinalProgress correctly rejects the now-stale checkpoint.
    private bool CheckpointHeld(string body,IReadOnlyList<string> ready,int through,string state,bool stopped=false)
    {
        if(body.Length==0||!Allowed()||Identity==null)return false;
        if(_segment.Length==0)
        {
            if(stopped)return false;
            _work.SourceUserEventId=_owner.UserEventId;
            _segment=_work.ReserveFinalProgress(body,_generation.Response.AttemptId,_permission,Identity);
            if(_segment.Length==0)return false;
        }
        if(!_work.SetFinalProgress(_segment,body,ready,through,stopped))return false;
        var expression=_work.FinalProgressSnapshot(_segment);
        var checkpoint=new PublicFinalCheckpoint(GenerationId,++_revision,state,ready.Count,_work.FinalProgressChunks(_segment),_reason);
        var saved=_store.CapturePublicExpression(_generation,expression,_work.Outcome,checkpoint,_sources);
        if(!saved.Succeeded){_work.FreezeFinalProgress(_segment);return false;}
        _work.ReplaceProgressArchive(_segment,_store,saved.Key);LatestKey=saved.Key;
        _body=body;_ready.Clear();_ready.AddRange(ready);_through=through;
        _work.FlushArchive();return true;
    }
    internal void ValidateTerminal(JsonElement response)
    {
        if(_segment.Length==0)return;
        if(S(response,"id")!=_response||!response.TryGetProperty("output",out var output)||output.ValueKind!=JsonValueKind.Array
            ||_output>=output.GetArrayLength())throw Mismatch();
        var item=output[_output];
        if(S(item,"id")!=_item||S(item,"type")!="message"||S(item,"role")!="assistant"
            ||S(item,"phase") is not ("" or "final_answer")||output.EnumerateArray().Count(x=>S(x,"id")==_item)!=1)throw Mismatch();
    }
    internal bool Seal(string wire,bool selected)
    {
        if(_segment.Length==0)return false;
        _work.HoldArchivePublication(_generation.Response.AttemptId);
        try
        {
            string body=MemoryStore.Clean(_project(wire));
            var committed=_work.FinalProgressCommitted(_segment);
            if(committed.Through>body.Length||body[..committed.Through]!=_body[..committed.Through])throw Mismatch();
            // The not-yet-submitted suffix may be revised by a terminal item. Its
            // preceding immutable checkpoint remains available under the same owner.
            string[] ready=selected?committed.Chunks:committed.Chunks.Concat(BubbleChunks.Split(body[committed.Through..])).ToArray();
            int through=selected?committed.Through:body.Length;
            if(!CheckpointHeld(body,ready,through,selected?"selected_final_closed":"deferred_item_closed"))throw new OpenAIProtocolException("openai_public_checkpoint_failed","公开片段接续未能保存；已有显示记录保留，本轮其余内容未交付。");
            _sealed=true;_selected=selected;BindSource();return true;
        }
        finally{_work.PublishAfterArchive(_generation.Response.AttemptId);}
    }
    private void BindSource()
    {
        var saved=_store.ReadPublicExpressionForCapture(LatestKey);
        if(saved!=null&&_generation.PublicExpressionFootprint is {} source)
            _generation.PublicExpressionFootprint=source with {GeneratedSources=source.GeneratedSources.Append(
                new(LatestKey,saved.BodyHash,MemoryStore.PublicExpressionImmutableIdentity(saved))).Distinct().ToArray()};
    }
    internal void End()
    {
        if(!_sealed&&_body.Length>0)
        {
            _work.HoldArchivePublication(_generation.Response.AttemptId);
            try
            {
                bool stopped=_work.Outcome is not "running";
                if(_segment.Length>0)
                {
                    var fixedPart=_work.FinalProgressCommitted(_segment);
                    _=CheckpointHeld(_body,fixedPart.Chunks,fixedPart.Through,"response_not_completed",stopped);
                }
                else if(!stopped)_=CheckpointHeld(_body,Array.Empty<string>(),0,"interrupted_before_unit");
            }
            finally{_work.PublishAfterArchive(_generation.Response.AttemptId);}
        }
        if(_generation.Error.Length>0)_work.Finish(_owner.IsCancelled?"cancelled":"failed");
        _work.FlushArchive();
        if(LatestKey.Length>0)_store.RecordPublicStreamTerminal(LatestKey,_generation);
    }
    internal BubbleDisplayPlan? FinalPlan(string body)
    {
        if(!_selected||!_sealed||_body!=body)return null;
        var plan=BubbleChunks.BuildStreamingPlan(body,_ready.Concat(BubbleChunks.Split(body[_through..])).ToArray());
        return plan.IsValid?plan:null;
    }
    internal StreamAppearanceEvidence[] Appearance()=>_segment.Length==0?Array.Empty<StreamAppearanceEvidence>():_work.FinalProgressAppearance(_segment);
    internal PublicFinalCanonical? Canonical=>_selected&&LatestKey.Length>0?new(GenerationId,LatestKey):null;
    private static OpenAIProtocolException Mismatch()=>new("openai_public_final_mismatch","最终答复与已提交显示的公开前缀不一致；原片段及显示状态保留，本轮其余内容和控制未交付。");
}

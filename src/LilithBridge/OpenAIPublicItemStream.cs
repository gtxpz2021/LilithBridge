using System.Text;
using System.Text.Json;

namespace LilithBridge;

// One closed, explicitly public commentary item, not arbitrary token output.
// A separate final_answer is still validated and delivered by the old path.
internal sealed record PublicStreamItemIdentity(string ResponseId,string ItemId,int OutputIndex,int ContentIndex,string Phase,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? WireTextHash=null);
internal sealed record ClosedPublicStreamItem(PublicStreamItemIdentity Identity,string Text,bool TerminalValidated=false);
internal sealed record PublicItemReceipt(PublicStreamItemIdentity Identity,int WireChars,string Disposition);

internal sealed class OpenAIPublicItemStream
{
    private readonly Func<ClosedPublicStreamItem,bool> _offer;
    private readonly StringBuilder _text=new();
    private string _response="",_item="",_doneText="";
    private int _index=-1,_sequence=-1;
    private bool _part,_done,_blocked;
    internal ClosedPublicStreamItem? Accepted { get; private set; }
    internal bool AllowEarly {get;set;}=true;
    internal PublicFinalDraft? FinalDraft {get;set;}
    internal PublicStreamItemIdentity? FinalItem {get;private set;}
    internal List<PublicItemReceipt> TerminalItems {get;}=new();
    internal OpenAIPublicItemStream(Func<ClosedPublicStreamItem,bool> offer)=>_offer=offer;
    private static string S(JsonElement e,string name)=>e.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??"":"";
    private static int N(JsonElement e,string name)=>e.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetInt32(out var n)?n:-1;
    private static bool Id(string s)=>s.Length is >0 and <=256 && s.All(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
    private bool Matches(JsonElement e)=>S(e,"item_id")==_item&&N(e,"output_index")==_index&&N(e,"content_index")==0;
    private static string? PublicText(JsonElement item)
    {
        if(S(item,"type")!="message"||S(item,"role")!="assistant"||S(item,"phase")!="commentary"||S(item,"status")!="completed"
            ||!item.TryGetProperty("content",out var c)||c.ValueKind!=JsonValueKind.Array||c.GetArrayLength()!=1)return null;
        var p=c[0];return S(p,"type")=="output_text"?S(p,"text"):null;
    }
    internal void Observe(JsonElement e)
    {
        FinalDraft?.Observe(e);
        string type=S(e,"type");
        if(!AllowEarly||Accepted!=null||_blocked)return;
        int sequence=N(e,"sequence_number");
        if(sequence<0||sequence<=_sequence){_blocked=true;return;}_sequence=sequence;
        if(type=="response.created")
        {if(e.TryGetProperty("response",out var r))_response=S(r,"id");return;}
        if(type=="response.output_item.added")
        {
            if(!e.TryGetProperty("item",out var item)||S(item,"type")!="message"||S(item,"role")!="assistant"||S(item,"phase")!="commentary")return;
            if(_item.Length>0){_blocked=true;return;}
            _item=S(item,"id");_index=N(e,"output_index");
            if(!Id(_response)||!Id(_item)||_index<0)_blocked=true;
            return;
        }
        if(_item.Length==0)return;
        if(type=="response.content_part.added"&&Matches(e))
        {
            if(_part||!e.TryGetProperty("part",out var p)||S(p,"type")!="output_text"||S(p,"text").Length>0){_blocked=true;return;}
            _part=true;return;
        }
        if(type=="response.output_text.delta"&&Matches(e))
        {
            if(!_part||_done||!e.TryGetProperty("delta",out var d)||d.ValueKind!=JsonValueKind.String){_blocked=true;return;}
            _text.Append(d.GetString());if(_text.Length>4096)_blocked=true;return;
        }
        if(type=="response.output_text.done"&&Matches(e))
        {
            if(!_part||_done||(_doneText=S(e,"text"))!=_text.ToString()){_blocked=true;return;}
            _done=true;return;
        }
        if(type=="response.output_item.done"&&S(e,"item_id")==_item){_blocked=true;return;} // malformed alias shape
        if(type!="response.output_item.done"||N(e,"output_index")!=_index)return;
        if(!e.TryGetProperty("item",out var closed)||S(closed,"id")!=_item||!_done||PublicText(closed)!=_doneText){_blocked=true;return;}
        // Never project incomplete JSON, application control tags, code fences,
        // tool arguments, reasoning summaries, or an unclosed text part.
        string t=_doneText.Trim();
        if(t.Length==0||t.StartsWith('{')||t.StartsWith('[')||t.Contains('<')||t.Contains("```",StringComparison.Ordinal))return;
        var candidate=new ClosedPublicStreamItem(new(_response,_item,_index,0,"commentary"),_doneText);
        _blocked=true; // A failed save/offer must not be retried by another done event.
        if(_offer(candidate))Accepted=candidate;
    }
    internal string? ValidateTerminal(JsonElement response)
    {
        FinalDraft?.ValidateTerminal(response);
        if(Accepted is not {} accepted)return null;
        if(S(response,"id")!=accepted.Identity.ResponseId||!response.TryGetProperty("output",out var output)||output.ValueKind!=JsonValueKind.Array
            ||accepted.Identity.OutputIndex>=output.GetArrayLength())throw Mismatch();
        var item=output[accepted.Identity.OutputIndex];
        if(S(item,"id")!=accepted.Identity.ItemId||TerminalText(item)!=accepted.Text
            ||output.EnumerateArray().Count(i=>S(i,"id")==accepted.Identity.ItemId)!=1)throw Mismatch();
        return accepted.Identity.ItemId;
    }
    private static string? TerminalText(JsonElement item)
    {
        if(S(item,"type")!="message"||S(item,"role")!="assistant"||S(item,"status") is not ("" or "completed")
            ||!item.TryGetProperty("content",out var parts)||parts.ValueKind!=JsonValueKind.Array)return null;
        var result=new StringBuilder();
        foreach(var part in parts.EnumerateArray())
        {if(S(part,"type")!="output_text"||!part.TryGetProperty("text",out var t)||t.ValueKind!=JsonValueKind.String)return null;result.Append(t.GetString());}
        return result.ToString(); // Within ONE provider item, never across messages.
    }
    internal string? PrepareTerminal(JsonElement response)
    {
        var output=response.GetProperty("output").EnumerateArray().ToArray();
        var messages=output.Select((item,index)=>(item,index,text:TerminalText(item))).Where(x=>x.text!=null).ToArray();
        bool calls=output.Any(i=>S(i,"type")=="function_call");
        int finalIndex=calls||messages.Length==0?-1:messages.Last().index;
        int queued=0;
        foreach(var entry in messages)
        {
            string text=entry.text!;
            var identity=new PublicStreamItemIdentity(S(response,"id"),S(entry.item,"id"),entry.index,-1,S(entry.item,"phase"),MemoryStore.SemanticTextHash(text));
            bool progressive=FinalDraft?.Identity?.ItemId==identity.ItemId&&FinalDraft.Seal(text,entry.index==finalIndex);
            bool already=progressive||Accepted?.Identity.ItemId==identity.ItemId;
            if(entry.index==finalIndex)
            {
                FinalItem=identity;
                TerminalItems.Add(new(identity,text.Length,progressive?"final_progress_selected;handoff_required":already?"early_item_is_complete_response;no_duplicate_display":"selected_final_item;original_control_parser"));
            }
            else if(already)TerminalItems.Add(new(identity,text.Length,"early_public_item_retained"));
            else
            {
                bool offered=false;
                if(queued<16&&Id(identity.ResponseId)&&Id(identity.ItemId))
                    offered=_offer(new(identity,text,TerminalValidated:true));
                if(offered)queued++;
                TerminalItems.Add(new(identity,text.Length,offered?"terminal_public_projection_queued;embedded_controls_not_applied":"not_presented;unsupported_projection_or_capacity_or_sources"));
            }
        }
        return FinalItem?.ItemId;
    }
    private static OpenAIProtocolException Mismatch()=>new("openai_public_item_mismatch","已呈现公开片段与最终响应不一致；片段身份和显示记录保留，本轮其余回复及控制未交付。");
}

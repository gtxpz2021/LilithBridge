using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    internal const string MethodManageTool="method_manage",MethodReadTool="method_read";
    // Internal offline seam after the real consumer, before result ownership checks.
    internal Action<DialogueRequest>? MethodOutcomeBoundaryForTests {get;set;}
    private static object Field(string type,string description)=>new{type,description};
    private static object MethodToolDefinition(bool manage)=>new {
        type="function",function=new {
            name=manage?MethodManageTool:MethodReadTool,
            description=manage?"可选：put 创建/修订一个会话内单步取材办法，retire 显式放下。可以不用或改主意；保存不执行、不授予权限，也不证明有效。"
                :"可选：显式采用当前版本，执行一次既有 read。可继续直接回答或直接 review_context；不读取旧轮 ref、不追加权限或预算。",
            parameters=new {type="object",properties=manage?new Dictionary<string,object>{
                ["action"]=new{type="string",@enum=new[]{"put","retire"}},["method_id"]=Field("string","会话目录身份，不是材料来源 ref。"),
                ["previous_revision"]=Field("integer","新建为0；修订/放下须精确匹配当前版本。"),
                ["title"]=Field("string","普通文字名称。"),["when"]=Field("string","普通文字适用情况，不是系统指令。"),
                ["reason"]=Field("string","本次创建/修改缘由。"),["question_template"]=Field("string","read 的自足问题；用 {{name}} 参数槽。"),
                ["query_template"]=Field("string","可空的词面线索；同样只作文字替换。"),
                ["selector"]=new{type="string",@enum=new[]{"discovery","current_refs"}},
                ["parameters"]=new{type="array",items=new{type="string"}},["based_on"]=Field("string","可选解释，不能减少宿主依赖。")
            }:new Dictionary<string,object>{
                ["method_id"]=Field("string","最新宿主数据中列出的方法目录 ID。"),["revision"]=Field("integer","精确采用的版本。"),
                ["bindings"]=new{type="object",additionalProperties=new{type="string"},description="仅字面替换声明参数，不执行代码。"},
                ["source_refs"]=new{type="array",items=new{type="string"},description="current_refs 仅可选本轮实际 review 返回的 ref；discovery 不接收此字段。"},
                ["offset"]=Field("integer","current_refs 的单来源实际 next_offset；不支持任意偏移。")},
                required=manage?new[]{"action","method_id","previous_revision"}:new[]{"method_id","revision","bindings"},additionalProperties=false}
        }};

    private static bool StrictFields(JsonElement root,params string[] fields)=>root.ValueKind==JsonValueKind.Object
        &&root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==root.EnumerateObject().Count()
        &&root.EnumerateObject().All(p=>fields.Contains(p.Name,StringComparer.Ordinal));
    private static string Text(JsonElement root,string key,int max,bool required=true)
    {
        if(!root.TryGetProperty(key,out var p)){if(!required)return "";throw new ArgumentException("missing_"+key);}
        if(p.ValueKind!=JsonValueKind.String || p.GetString()!.Length>max || required&&string.IsNullOrWhiteSpace(p.GetString()))
            throw new ArgumentException("invalid_"+key);
        return p.GetString()!;
    }
    private static int Number(JsonElement root,string key,int min,int max,bool required=true)
    {
        if(!root.TryGetProperty(key,out var p)){if(!required)return 0;throw new ArgumentException("missing_"+key);}
        if(p.ValueKind!=JsonValueKind.Number||!p.TryGetInt32(out int n)||n<min||n>max)throw new ArgumentException("invalid_"+key);return n;
    }
    private static string[] Strings(JsonElement root,string key,int max,int chars,bool required=false)
    {
        if(!root.TryGetProperty(key,out var p)){if(!required)return Array.Empty<string>();throw new ArgumentException("missing_"+key);}
        if(p.ValueKind!=JsonValueKind.Array||p.GetArrayLength()>max||p.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String
            ||string.IsNullOrWhiteSpace(v.GetString())||v.GetString()!.Length>chars))throw new ArgumentException("invalid_"+key);
        var a=p.EnumerateArray().Select(v=>v.GetString()!).ToArray();if(a.Distinct(StringComparer.Ordinal).Count()!=a.Length)throw new ArgumentException("duplicate_"+key);return a;
    }
    private static MethodAction ParseMethod(string json,bool manage)
    {
        using var doc=JsonDocument.Parse(json);var r=doc.RootElement;
        if(!StrictFields(r,manage?new[]{"action","method_id","previous_revision","title","when","reason","question_template","query_template","selector","parameters","based_on"}
            :new[]{"method_id","revision","bindings","source_refs","offset"}))throw new ArgumentException("unknown_or_duplicate_fields");
        string id=Text(r,"method_id",64);
        if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z"))throw new ArgumentException("invalid_method_id");
        if(manage)
        {
            string action=Text(r,"action",8);int previous=Number(r,"previous_revision",0,int.MaxValue-1);
            if(action=="retire")
            {if(!StrictFields(r,"action","method_id","previous_revision"))throw new ArgumentException("retire_fields");return new(action,id,previous);}
            if(action!="put")throw new ArgumentException("invalid_action");
            string selector=Text(r,"selector",16);if(selector is not ("discovery" or "current_refs"))throw new ArgumentException("invalid_selector");
            var d=new ReadMethodDefinition(id,previous+1,Text(r,"title",120),Text(r,"when",300),Text(r,"reason",300),
                Text(r,"question_template",500),Text(r,"query_template",300,false),selector,Strings(r,"parameters",8,32,true),Text(r,"based_on",500,false));
            if(!ReadMethodTemplates.Valid(d))throw new ArgumentException("invalid_template_parameters");return new(action,id,previous,d);
        }
        int revision=Number(r,"revision",1,int.MaxValue);var bindings=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!r.TryGetProperty("bindings",out var b)||b.ValueKind!=JsonValueKind.Object)throw new ArgumentException("invalid_bindings");
        foreach(var p in b.EnumerateObject())
            if(p.Name.Length>32||p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length>300
                ||!bindings.TryAdd(p.Name,p.Value.GetString()!))throw new ArgumentException("invalid_bindings");
        return new("read",id,0,Revision:revision,Bindings:bindings,Refs:Strings(r,"source_refs",6,32),Offset:Number(r,"offset",0,ReviewContext.MaxRecordOffset,false),HasRefs:r.TryGetProperty("source_refs",out _));
    }
    private static string MethodOfferError(DialogueRequest r,MethodOffer? o,string id="",int revision=0)
    {
        if(o==null)return "not_declared";
        var live=r.ReviewHandoff?.Config(r)??r.B1ReadConfig;
        r.ReviewStore?.SyncReadMethods(live);
        if(r.IsCancelled||o.RequestId!=r.Id||!live.SessionReadMethodsEnabled||!r.ReviewToolEnabled
            ||r.MethodCapability?.Permits(o.CapabilityGeneration)==false||!o.Owner.Matches(o,id,revision))return "stale_offer";
        if(o.MaterialRevision!=r.MethodMaterialRevision)return "materials_changed";
        if(o.TrackingError.Length>0)return o.TrackingError;
        return r.ReviewStore?.MethodSourcesPermitted(o.Sources,r)==true?"":"source_changed";
    }
    private static string? ReadMethodCall(string tool,string json,string id,JsonElement message,
        IReadOnlyList<RejectedToolCall>? extras,ApiResult envelope)
    {
        var r=envelope.DialogueRequest!;var o=envelope.NativeToolOffer?.Methods;
        MethodAction? action=null;ReviewToolArgs? read=null;string error="";bool manage=tool==MethodManageTool;
        try
        {
            action=ParseMethod(json,manage);error=MethodOfferError(r,o,manage?"":action.Id,action.Revision);
            if(error.Length==0&&!manage)
            {
                var d=o!.Methods.SingleOrDefault(m=>m.Definition.Id==action.Id&&m.Definition.Revision==action.Revision)?.Definition;
                if(d==null)error="method_unavailable";
                else if(d.Selector=="discovery"&&(action.Refs!.Length>0||action.Offset!=0
                    ||action.HasRefs))error="discovery_selector_arguments";
                else if(d.Selector=="current_refs"&&(action.Refs!.Length==0||action.Refs.Any(reference=>!o.Refs.Any(v=>v.Id==reference))
                    ||action.Offset>0&&(action.Refs.Length!=1||!o.Refs.Single(v=>v.Id==action.Refs[0]).NextOffsets.Contains(action.Offset))))error="current_ref_or_offset_unavailable";
                else read=ReadMethodTemplates.Expand(d,action,out error);
                if(error.Length==0&&!r.TryAcceptReviewCall(false,out error))error="limit:"+error;
            }
        }
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){error=e is ArgumentException?e.Message:"invalid_arguments";}
        envelope.PendingNativeCall=new PendingNativeCall(manage?NativeToolKind.MethodManage:NativeToolKind.MethodRead,
            id,message.Clone(),read,error,extras,Method:action,MethodSnapshot:o);
        envelope.Response.Outcome=error.Length==0?"accepted_tool_call":"accepted_tool_call_invalid";return "";
    }
    private static MethodOffer? PrepareMethodOffer(DialogueRequest r,MemoryStore? memory)
    {
        var config=r.ReviewHandoff?.Config(r)??r.B1ReadConfig;
        if(memory==null)return null;
        var owner=memory.SyncReadMethods(config);
        if(!config.SessionReadMethodsEnabled)return null;
        r.MethodOwner??=owner;
        var snapshot=owner.Snapshot();
        // Current-query supply policy can hide a method without terminating it.
        // Only persistent identity/version/lifecycle loss removes its head.
        foreach(var m in snapshot.Methods)if(!memory.MethodSourcesCurrent(m.Sources))owner.Invalidate(m);
        snapshot=owner.Snapshot();
        var permitted=snapshot.Methods.Where(m=>memory.MethodSourcesPermitted(m.Sources,r)).ToArray();
        var sources=memory.CaptureMethodSources(r,out string error);
        if(r.MethodCapability?.Permits(r.MethodCapabilityGeneration)==false || !r.ReviewToolEnabled)error="stale_offer";
        var shown=error.Length==0?permitted:Array.Empty<ReadMethodVersion>();
        sources=(sources??Array.Empty<MethodSource>()).Concat(shown.SelectMany(m=>m.Sources)).Distinct().ToArray();
        var refs=r.ReviewSourceViews.Select(p=>new MethodRef(p.Key,p.Value.Where(v=>v.Truncated&&v.Offset+v.Text.Length<v.TotalChars)
            .Select(v=>v.Offset+v.Text.Length).Distinct().ToArray())).ToArray();
        return new(owner,r.Id,snapshot.Epoch,snapshot.Version,r.MethodCapabilityGeneration,r.MethodMaterialRevision,sources,shown,refs,error);
    }
    private static object MethodHostMessage(DialogueRequest r,MethodOffer o)
    {
        // Host message is replaced, never appended to the stored protocol tail.
        int calls=r.ReviewBudget?.Calls??r.ReviewCalls;
        var state=new {kind="lilith_session_method_state",schema_version=1,
            methods=o.Methods.Select(m=>new {definition=m.Definition,attempts=m.Attempts,last_outcome=m.LastOutcome}),
            source_tracking=o.TrackingError.Length==0?"available":o.TrackingError,
            host=new {request_id=r.Id,current_refs=o.Refs.Select(v=>new{source_ref=v.Id,next_offsets=v.NextOffsets}),
                used_reads=calls,remaining_reads=Math.Max(0,ReviewContext.MaxCallsPerTurn-calls),http_used=(r.ReviewBudget?.Http??r.HttpAttempts)+1,
                request_http_used=r.HttpAttempts+1,
                remaining_http=Math.Max(0,r.RemainingHttpAttempts-1),current_http_counted=true,
                read_available=o.TrackingError.Length==0&&r.ReviewToolEnabled&&calls<ReviewContext.MaxCallsPerTurn
                    &&r.RemainingHttpAttempts>1&&r.ReviewBudget?.AllowsNewSteps!=false}};
        return new {role="user",content="[会话内可选方法·宿主数据]\n"+ReviewContext.Serialize(state)};
    }
    // Permission predicate shared by HTTP and delivery consumers; lifecycle
    // cancellation and delivery settlement remain with the caller.
    internal static bool MethodProtocolPermitted(DialogueRequest r)
    {
        if(r.MethodProtocolSources.Count==0)return true;
        if(r.IsCancelled||r.MethodCapability?.Permits(r.MethodCapabilityGeneration)==false||r.ReviewStore==null)return false;
        var live=r.ReviewHandoff?.Config(r)??r.B1ReadConfig;
        var owner=r.ReviewStore.SyncReadMethods(live);
        return live.SessionReadMethodsEnabled&&r.MethodProtocolEpoch==owner.Snapshot().Epoch
            &&r.ReviewStore.TryUseReviewSources(r.MethodProtocolSources,Array.Empty<MemoryTurn>(),"",
                r.ReviewCurrentText,r.B1ReadConfig,()=>true);
    }
}
